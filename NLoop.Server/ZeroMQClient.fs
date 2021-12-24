namespace NLoop.Server

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open DotNetLightning.Utils.Primitives
open FSharp.Control
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.DataEncoders
open NLoop.Domain
open FSharp.Control.Tasks

open NLoop.Server
open NetMQ
open NetMQ.Sockets

type FilterType =
  | RawTx
  | HashTx
  | RawBlock
  | HashBlock
  | Sequence
  with
  member this.Topic =
    match this with
    | RawTx -> "rawtx"
    | HashTx -> "hashtx"
    | RawBlock -> "rawblock"
    | HashBlock -> "hashblock"
    | Sequence -> "sequence"

type ZmqNotification = {
  CryptoCode: SupportedCryptoCode
  Address: string
  FilterType: FilterType
}
  with
  member this.AddressUri = Uri(this.Address)

[<AutoOpen>]
module ZmqMsg =
  type SequenceMsg =
    | BlockHashConnected of uint256
    | BlockHashDisconnected of uint256
    | TransactionHashAdded of txHash: uint256 * mempoolIndex: uint64
    | TransactionHashRemove of txHash: uint256 * mempoolIndex: uint64

[<AutoOpen>]
module private ZmqHelpers =
  let utf8ToBytes (str: string) = System.Text.Encoding.UTF8.GetBytes(str)
  let bytesToUtf8 (b: byte[]) = System.Text.Encoding.UTF8.GetString b
  let hex = HexEncoder()
  let hashblockB = "hashblock" |>  utf8ToBytes
  let hashtxB = "hashtx" |>  utf8ToBytes
  let rawblockB = "rawblock" |>  utf8ToBytes
  let rawtxB = "rawtx" |> utf8ToBytes
  let sequenceB = "sequence" |> utf8ToBytes

type private OnBlock = SupportedCryptoCode * Block -> unit
/// We wanted to use `Channel` for publishing new blocks.
/// But sadly NetMQ sockets are not thread safe, and we had to use synchronous
/// primitive (in this case event aggregator) to get things done right.
type ZmqClient(cc: SupportedCryptoCode,
               opts: IOptions<NLoopOptions>,
               onBlock: OnBlock,
               ?ct: CancellationToken) as this =
  let sock = new SubscriberSocket()
  let runtime = new NetMQRuntime()
  let ct = defaultArg ct CancellationToken.None

  do
    Task.Factory.StartNew(this.WorkerThread, TaskCreationOptions.LongRunning)
    |> ignore

  member private this.WorkerThread() =
    for bTypes in seq [RawBlock; HashBlock] do
      sock.Subscribe bTypes.Topic
    sock.Connect opts.Value.ChainOptions.[cc].ZmqAddress
    while not <| ct.IsCancellationRequested do
      let m = sock.ReceiveMultipartMessage(3)
      let topic, body, sequence = (m.Item 0), (m.Item 1), (m.Item 2)
      if topic.Buffer = rawblockB then
        let b = Block.Parse(body.Buffer |> hex.EncodeData, Network.RegTest)
        onBlock(cc, b)

  interface IDisposable with
    member this.Dispose () =
      try
        if sock.IsDisposed then () else sock.Dispose()
        runtime.Dispose()
      with
      | ex ->
        printfn $"Failed to dispose {nameof(ZmqClient)}. this should never happen: {ex}"


type ZmqBlockchainListener(opts: IOptions<NLoopOptions>,
               logger: ILogger<ZmqBlockchainListener>,
               client: IBlockChainClient,
               actor: ISwapActor) =

  let zmqClients = ResizeArray()
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
        |> Seq.map(fun s -> actor.Execute(s, cmd, nameof(ZmqBlockchainListener)))
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
      |> Seq.map(fun s -> actor.Execute(s, cmd, nameof(ZmqBlockchainListener)))
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
          let b = {
            Height = currentBlock.Height + BlockHeightOffset16.One
            Block = b
          }
          do! newChainTipAsync(cc, b)
        else
          let getBlock =
            client.GetBlock >> Async.AwaitTask
          let! newBlock = getBlock (b.Header.GetHash())
          if newBlock.Height = currentBlock.Height then
            ()
          elif newBlock.Height < currentBlock.Height then
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
              do! newChainTipAsync(cc, { Height = iHeight; Block = nextB })
              iHash <- nextB.Header.GetHash()
            //if height > prevBlock.Height
          ()
      with
      | ex ->
        logger.LogError($"Error while handling block {ex}")
  }


  interface IHostedService with
    member this.StartAsync(cancellationToken) = unitTask {
      opts.Value.OnChainCrypto
      |> Seq.iter(fun cc ->
        let network = opts.Value.GetNetwork(cc)
        currentTips.AddOrReplace(cc, BlockWithHeight.Genesis network)
        let c = new ZmqClient(cc, opts, this.OnBlock >> Async.Start, cancellationToken)
        zmqClients.Add(c)
      )
    }
    member this.StopAsync(cancellationToken) = unitTask {
      for c in zmqClients do
        try
          (c :> IDisposable).Dispose()
        with
        | ex ->
          logger.LogDebug $"socket already closed"
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
