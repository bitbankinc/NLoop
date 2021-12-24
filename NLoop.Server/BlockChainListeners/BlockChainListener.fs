namespace NLoop.Server

open System.Collections.Concurrent
open System.Threading.Tasks
open DotNetLightning.Utils
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NLoop.Domain

type BlockchainListener(opts: IOptions<NLoopOptions>,
                        loggerFactory: ILoggerFactory,
                        client: IBlockChainClient,
                        actor: ISwapActor) =

  let logger: ILogger<BlockchainListener> = loggerFactory.CreateLogger<_>()
  let currentTips =
    let d = ConcurrentDictionary<SupportedCryptoCode, BlockWithHeight>()
    opts.Value.OnChainCrypto
    |> Array.iter(fun cc ->
      let n = opts.Value.GetNetwork cc
      d.TryAdd(cc, BlockWithHeight.Genesis n) |> ignore
    )
    d

  let swaps = ConcurrentDictionary<SwapId, _>()

  let newChainTipAsync(cc, newB) = async {
      let cmd =
        (newB, cc)
        |> Swap.Command.NewBlock
      do!
        swaps.Keys
        |> Seq.map(fun s -> actor.Execute(s, cmd, nameof(BlockchainListener)))
        |> Task.WhenAll
        |> Async.AwaitTask
      let prevTips = currentTips.[cc]
      match currentTips.TryUpdate(cc, newB, prevTips) with
      | true -> ()
      | false ->
        logger.LogError($"Failed to update prevBlocks. This should never happen")
  }

  let onBlockDisconnected blockHash = async {
    let cmd = (Swap.Command.UnConfirmBlock(blockHash))
    do!
      swaps.Keys
      |> Seq.map(fun s -> actor.Execute(s, cmd, nameof(BlockchainListener)))
      |> Task.WhenAll
      |> Async.AwaitTask
  }

  member this.OnBlock(cc, b: Block) = async {
      try
        let currentBlock = currentTips.[cc]
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
          do! newChainTipAsync(cc, bh)
        else
          let getBlock =
            client.GetBlock >> Async.AwaitTask
          let! newBlock = getBlock (b.Header.GetHash())
          if newBlock.Height <= currentBlock.Height then
            // no reorg (stale tip). we can safely ignore.
            ()
          else
          let! maybeAncestor =
            BlockWithHeight.rewindToNextOfCommonAncestor getBlock currentBlock newBlock
          match maybeAncestor with
          | None ->
            logger.LogCritical "The block with no common ancestor detected, this should never happen"
            assert false
            return ()
          | Some (ancestor, disconnectedBlockHashes) ->
            for h in disconnectedBlockHashes do
              do! onBlockDisconnected h
            do! newChainTipAsync(cc, ancestor)
            let mutable iHeight = ancestor.Height
            let mutable iHash = ancestor.Block.Header.GetHash()
            let! bestBlockHash = client.GetBestBlockHash() |> Async.AwaitTask
            while bestBlockHash <> iHash do
              iHeight <- iHeight + BlockHeightOffset16.One
              let! nextB = client.GetBlockFromHeight(iHeight) |> Async.AwaitTask
              let nextBH = { Height = iHeight; Block = nextB }
              do! newChainTipAsync(cc, nextBH)
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

