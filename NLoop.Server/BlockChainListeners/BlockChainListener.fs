namespace NLoop.Server

open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NLoop.Domain
open FSharp.Control.Tasks

[<AutoOpen>]
module private BlockchainListenerHelpers =
  /// searches for a common ancestor of two blocks, and returns its next block towards
  /// a tip given as a last argument as its first return value.
  /// The second return value is blocks those which must be disconnected.
  ///
  /// Given the following blockchain
  /// block0 ----> block1 ---> block2_0 ---> block3_0
  ///                   \
  ///                    -----> block2_1 ----> block3_1
  /// `rewindToNextOfCommonAncestor (getBlock) block3_0 block3_1`
  /// will return `Some(block2_1, [block2_0; block3_0])`.
  ///
  /// `rewindToNextOfCommonAncestor (getBlock) block1 block3_0`
  /// will return `Some(block2_0, [])` .
  ///
  /// If there were no common ancestor, it will return None, this should never happen if the blocks are from the same
  /// blockchain.
  let rewindToNextOfCommonAncestor getBlock oldTip newTip =
    let rec loop
      (getBlock: uint256 -> Async<BlockWithHeight>)
      (blockDisconnected: uint256 list)
      (oldTip: BlockWithHeight)
      (newTip: BlockWithHeight)
      (getRewindLimit: unit -> BlockHeight) =
      async {
        let! ct = Async.CancellationToken
        ct.ThrowIfCancellationRequested()
        let oldHash = oldTip.Block.Header.GetHash()
        let newHash = newTip.Block.Header.GetHash()
        assert(oldHash <> newHash)
        let rewindLimit = getRewindLimit()
        if newTip.Height <= rewindLimit then
          return (newTip, blockDisconnected) |> Some
        else
        if oldTip.Block.Header.HashPrevBlock = newTip.Block.Header.HashPrevBlock then
          let d = oldHash::blockDisconnected
          return (newTip, d) |> Some
        elif oldHash = newTip.Block.Header.HashPrevBlock then
          return (newTip, blockDisconnected) |> Some
        elif oldTip.Height <= newTip.Height then
          let! newTipPrev = getBlock newTip.Block.Header.HashPrevBlock
          return! loop getBlock blockDisconnected oldTip newTipPrev getRewindLimit
        elif oldTip.Height > newTip.Height then
          let d = oldHash::blockDisconnected
          let! oldTipPrev = getBlock oldTip.Block.Header.HashPrevBlock
          return! loop getBlock d oldTipPrev newTip getRewindLimit
        else
          return failwith "unreachable!"
      }
    loop getBlock [] oldTip newTip
type BlockchainListener(opts: IOptions<NLoopOptions>,
                        loggerFactory: ILoggerFactory,
                        getBlockchainClient: GetBlockchainClient,
                        cc: SupportedCryptoCode,
                        getNetwork: GetNetwork,
                        actor: ISwapActor) as this =

  let logger: ILogger<BlockchainListener> = loggerFactory.CreateLogger<_>()
  let mutable _currentTip =
    getNetwork cc
    |> BlockWithHeight.Genesis

  let _currentTipLockObj = obj()

  let swaps = ConcurrentDictionary<SwapId, _>()

  let newChainTipAsync newB = task {
      logger.LogDebug $"New blockchain tip {newB} for {cc}"
      let cmd =
        (newB, cc)
        |> Swap.Command.NewBlock
      do!
        swaps.Keys
        |> Seq.map(fun s -> actor.Execute(s, cmd, nameof(BlockchainListener)))
        |> Task.WhenAll
      this.CurrentTip <- newB
  }

  let onBlockDisconnected blockHash = unitTask {
    let cmd = (Swap.Command.UnConfirmBlock(blockHash))
    do!
      swaps.Keys
      |> Seq.map(fun s -> actor.Execute(s, cmd, nameof(BlockchainListener)))
      |> Task.WhenAll
  }

  member this.CurrentTip
    with get() = _currentTip
    and set v =
      lock _currentTipLockObj <| fun () -> _currentTip <- v

  /// Whatever we use for listening to the tip of the blockchain (e.g. Zeromq or long-polling),
  /// It is always possible that we miss a block.
  /// This method assures the safety in that case by detecting the skip, and supplementing the missed block
  /// by querying against the blockchain.
  member this.OnBlock( b: Block, getRewindLimit, ?ct: CancellationToken) = unitTask {
      let ct = defaultArg ct CancellationToken.None
      try
        let client = getBlockchainClient cc
        let currentBlock = this.CurrentTip
        let newHeaderHash = b.Header.GetHash()
        let currentHeaderHash = currentBlock.Block.Header.GetHash()
        if currentHeaderHash = newHeaderHash then
          // nothing we should do.
          ()
        elif currentHeaderHash = b.Header.HashPrevBlock then
          let bh = {
            Height = currentBlock.Height + BlockHeightOffset16.One
            Block = b
          }
          do! newChainTipAsync bh
        else
          let! newBlock = client.GetBlock (b.Header.GetHash())
          if newBlock.Height <= currentBlock.Height then
            // no reorg (stale tip). we can safely ignore.
            ()
          else
          let! maybeAncestor =
            rewindToNextOfCommonAncestor (client.GetBlock >> Async.AwaitTask) currentBlock newBlock getRewindLimit
            |> fun a -> Async.StartAsTask(a, TaskCreationOptions.None, ct)
          match maybeAncestor with
          | None ->
            logger.LogCritical "The block with no common ancestor detected, this should never happen"
            assert false
            return ()
          | Some (ancestor, disconnectedBlockHashes) ->
            for h in disconnectedBlockHashes do
              do! onBlockDisconnected h
            ct.ThrowIfCancellationRequested()
            do! newChainTipAsync ancestor
            let mutable iHeight = ancestor.Height
            let mutable iHash = ancestor.Block.Header.GetHash()
            let! bestBlockHash = client.GetBestBlockHash() |> Async.AwaitTask
            while bestBlockHash <> iHash do
              ct.ThrowIfCancellationRequested()
              iHeight <- iHeight + BlockHeightOffset16.One
              let! nextB = client.GetBlockFromHeight(iHeight) |> Async.AwaitTask
              let nextBH = { Height = iHeight; Block = nextB }
              do! newChainTipAsync nextBH
              iHash <- nextB.Header.GetHash()
          ()
      with
      | ex ->
        logger.LogError($"Error while handling block {ex}")
  }

  interface ISwapEventListener with
    member this.RegisterSwap(id: SwapId) =
      if not <| swaps.TryAdd(id, ()) then
        logger.LogError($"Failed to add swap id {id}")

    member this.RemoveSwap(swapId) =
      if swaps.TryRemove(swapId) |> fst then
        ()
      else
        logger.LogError($"Failed to stop listening to {swapId}. This should never happen")

