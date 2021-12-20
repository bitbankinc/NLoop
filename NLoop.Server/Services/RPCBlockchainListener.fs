namespace NLoop.Server.Services

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open FSharp.Control
open FSharp.Control.Tasks.Affine

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

open NBitcoin
open NBitcoin
open NBitcoin.RPC

open NLoop.Domain
open NLoop.Server
open NLoop.Server.Actors

type RPCBlockchainListener(opts: IOptions<NLoopOptions>, actor: ISwapActor, logger: ILogger<RPCBlockchainListener>) as this =
  inherit BackgroundService()

  let swaps = ConcurrentDictionary<SwapId, _>()

  let rewindToBranchingPoint
    (cc: SupportedCryptoCode)
    (cli: RPCClient)
    (block: Block, newHeight: BlockHeight) = task {
    let mutable h1 = this.CurrentHeights |> Map.find(cc) |> fun (v: SlimChainedBlock) -> v.Clone()
    let mutable h2 = { Height = newHeight; HeaderHash = block.Header.GetHash(); PrevHash = block.Header.HashPrevBlock }
    while not <| (h1.PrevHash <> h2.PrevHash) do
      let rewindH1() = task {
        let! b = cli.GetBlockAsync(h1.PrevHash)
        h1 <- {
          HeaderHash = b.Header.GetHash();
          Height = h1.Height.Value - 1u |> BlockHeight
          PrevHash = b.Header.HashPrevBlock
        }
      }
      let rewindH2() = task {
        let! b = cli.GetBlockAsync(h2.PrevHash)
        h2 <- {
          HeaderHash = b.Header.GetHash();
          Height = h2.Height.Value - 1u |> BlockHeight
          PrevHash = b.Header.HashPrevBlock
        }
      }
      if h1.Height >= h2.Height then
        do! rewindH1()
      elif h1.Height < h2.Height then
        do! rewindH2()
      else
        failwith "unreachable"

    this.CurrentHeights <-
      this.CurrentHeights |> Map.change cc (fun _ -> Some h2)
  }

  let rec commitOneBlock
    (cc: SupportedCryptoCode)
    (cli: RPCClient)
    (height: BlockHeight) = task {
      let h = height.Value
      let! block = cli.GetBlockAsync(h)
      let newHash = block.Header.GetHash()
      let { HeaderHash = oldHeaderHash; PrevHash = oldPrevHash } = this.CurrentHeights.[cc]
      if block.Header.HashPrevBlock <> oldPrevHash then
        do! rewindToBranchingPoint cc cli (block, height)
        do! catchUpTo cc cli height
      elif newHash = oldHeaderHash then
        ()
      else
        let cmd =
          (height, block, cc)
          |> Swap.Command.NewBlock
        do!
          swaps.Keys
          |> Seq.map(fun s -> actor.Execute(s, cmd, nameof(RPCBlockchainListener)))
          |> Task.WhenAll
        this.CurrentHeights <-
          this.CurrentHeights
          |> Map.change cc (fun _ -> Some { Height = height; HeaderHash = newHash; PrevHash = block.Header.HashPrevBlock })
    }
  and catchUpTo
    (cc: SupportedCryptoCode)
    (cli: RPCClient)
    (newHeight: BlockHeight) = task {
    let { SlimChainedBlock.Height = oldHeight; } = this.CurrentHeights.[cc]
    assert(oldHeight <= newHeight)
    for h in oldHeight.Value..newHeight.Value do
      do! commitOneBlock cc cli (BlockHeight h)
  }

  let mutable currentHeights: Map<SupportedCryptoCode, SlimChainedBlock> =
    opts.Value.OnChainCrypto
    |> Array.fold(fun acc c -> Map.add c SlimChainedBlock.Zero acc)
      Map.empty
  let currentHeightsLockObj = obj()

  member this.CurrentHeights
    with get () = currentHeights
    and set v =
      lock currentHeightsLockObj (fun () -> currentHeights <- v)

  override this.ExecuteAsync(ct) = unitTask {
    try
      let clis: (RPCClient * _) seq =
        opts.Value.OnChainCrypto
        |> Seq.distinct
        |> Seq.map(fun x -> (opts.Value.GetRPCClient x, x))

      while not <| ct.IsCancellationRequested do
        do! Task.Delay 5000
        for cli, cc in clis do
          let catchUpTo = catchUpTo cc cli
          let commitOneBlock = commitOneBlock cc cli
          let! info = cli.GetBlockchainInfoAsync()
          let newBlockNum = info.Blocks |> uint32 |> BlockHeight
          let isIBDDone = not <| (info.VerificationProgress < 1.0f)
          let currentBlock =
            this.CurrentHeights.[cc]
          let currentHeight = currentBlock.Height
          let firstTimeIBDDone = isIBDDone && currentBlock = SlimChainedBlock.Zero
          if not isIBDDone then ()
          elif firstTimeIBDDone then
            do! commitOneBlock newBlockNum
          elif newBlockNum = currentHeight then
            do! commitOneBlock newBlockNum
          elif newBlockNum = currentHeight + BlockHeightOffset32.One then
            do! commitOneBlock newBlockNum
          elif newBlockNum <= currentHeight + BlockHeightOffset32.One then
            do! commitOneBlock newBlockNum
          else if currentHeight < newBlockNum then
            // reorg
            do! catchUpTo newBlockNum
            ()
    with
    | :? OperationCanceledException ->
      logger.LogInformation($"Stopping {nameof(RPCBlockchainListener)}...")
    | ex ->
      logger.LogError($"{ex}")
  }

  interface IBlockChainListener with
    member this.CurrentHeight cc = this.CurrentHeights.[cc].Height

  interface ISwapEventListener with
    member this.RegisterSwap(id: SwapId) =
      if not <| swaps.TryAdd(id, ()) then
        logger.LogError($"Failed to add swap id {id}")

    member this.RemoveSwap(swapId) =
      if swaps.TryRemove(swapId) |> fst then
        ()
      else
        logger.LogError($"Failed to stop listening to {swapId}. This should never happen")

