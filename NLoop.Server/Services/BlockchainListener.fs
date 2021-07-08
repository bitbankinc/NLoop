namespace NLoop.Server.Services

open System.Collections.Concurrent
open System.Threading.Tasks
open DotNetLightning.Utils
open FSharp.Control.Tasks.Affine

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

open NBitcoin.RPC

open NLoop.Domain
open NLoop.Server
open NLoop.Server.Actors

type BlockchainListener(opts: IOptions<NLoopOptions>, actor: SwapActor, logger: ILogger<BlockchainListener>) =
  inherit BackgroundService()

  let swaps = ConcurrentDictionary<SwapId, _>()

  let mutable currentHeight = BlockHeight 0u

  override this.ExecuteAsync(ct) = unitTask {
    try
      let clis: (RPCClient * _) seq =
        opts.Value.OnChainCrypto
        |> Seq.distinct
        |> Seq.map(fun x -> (opts.Value.GetRPCClient x, x))

      while true do
        do! Task.Delay 5000
        ct.ThrowIfCancellationRequested()
        for (cli, cc) in clis do
          let! info = cli.GetBlockchainInfoAsync()
          let newBlockNum = info.Blocks |> uint32 |> BlockHeight
          let isIBDDone = not <| (info.VerificationProgress < 1.0f)
          if isIBDDone && currentHeight <> newBlockNum then
            currentHeight <- newBlockNum
            let cmd =
              (newBlockNum, cc)
              |> Swap.Command.NewBlock
            for s in swaps.Keys do
              do! actor.Execute(s, cmd, nameof(BlockchainListener))
    with
    | ex ->
      logger.LogError($"{ex}")
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

