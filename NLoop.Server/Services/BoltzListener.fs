namespace NLoop.Server.Services


open System
open System.Collections.Concurrent
open System.Threading.Tasks
open BoltzClient
open DotNetLightning.Utils
open FSharp.Control
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

open Microsoft.Extensions.Options
open NBitcoin
open NLoop.Domain
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.SwapServerClient

type BoltzListener(swapServerClient: ISwapServerClient,
                   logger: ILogger<BoltzListener>,
                   opts: IOptions<NLoopOptions>,
                   actor: ISwapActor
                   ) =

  inherit BackgroundService()
  let statuses = ConcurrentDictionary<SwapId, Task<Transaction>>()


  override  this.ExecuteAsync(ct) = unitTask {
      try

        while not <| ct.IsCancellationRequested do
          ct.ThrowIfCancellationRequested()
          let ts = statuses.Values |> Seq.cast<_> |> Array.ofSeq
          let index = Task.WaitAny(ts, -1, ct)
          if index <> -1 then
            let! tx = ts.[index] :?> Task<Transaction>
            logger.LogInformation $"boltz notified about their swap tx {tx.ToHex()}"
            let swapId = statuses.Keys |> Seq.item index
            do! actor.Execute(swapId, Swap.Command.CommitSwapTxInfoFromCounterParty(tx.ToHex()))
            match statuses.TryRemove(swapId) with
            | true, _ -> ()
            | false, _ ->
              logger.LogWarning($"Failed to remove swap {swapId} this should never happen.")
      with
      | :? OperationCanceledException ->
        logger.LogInformation($"Stopping {nameof(BoltzListener)}...")
      | ex ->
        logger.LogCritical($"Connection to Boltz server {opts.Value.BoltzUrl} failed!")
        logger.LogError($"{ex}")
        raise <| ex
    }

  override this.StartAsync(ct) =
    try
      // Just to check the connection on the startup.
      let _ = swapServerClient.CheckConnection(ct).GetAwaiter().GetResult()
      logger.LogInformation $"starting {nameof(BoltzListener)}..."
    with
    | ex ->
      logger.LogCritical $"Failed to connect to boltz-backend"
      raise <| ex
    base.StartAsync(ct)

  interface ISwapEventListener with
    member this.RegisterSwap(swapId: SwapId) =
      let t = swapServerClient.ListenToSwapTx(swapId)
      statuses.TryAdd(swapId, t)
      |> ignore

    member this.RemoveSwap(swapId) =
      statuses.TryRemove(swapId)
      |> ignore

