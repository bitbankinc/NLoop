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
        // Just to check the connection on the startup.
        let! _boltzVersion = swapServerClient.CheckConnection(ct).ConfigureAwait(false)

        // todo: handle cancellation
        while not <| ct.IsCancellationRequested do
          ct.ThrowIfCancellationRequested()
          let ts = statuses.Values |> Seq.cast<_> |> Array.ofSeq
          let index = ts |> Task.WaitAny
          let! tx = ts.[index] :?> Task<Transaction>
          let swapId = statuses.Keys |> Seq.item index
          do! actor.Execute(swapId, Swap.Command.CommitSwapTxInfoFromCounterParty(tx.ToHex()))
      with
      | :? OperationCanceledException ->
        logger.LogInformation($"Stopping {nameof(BoltzListener)}...")
      | ex ->
        logger.LogCritical($"Connection to Boltz server {opts.Value.BoltzUrl} failed!")
        logger.LogError($"{ex.Message}")
        raise <| ex
    }

  interface ISwapEventListener with
    member this.RegisterSwap(swapId: SwapId) =
      let t = swapServerClient.ListenToSwapTx(swapId)
      statuses.TryAdd(swapId, t)
      |> ignore

    member this.RemoveSwap(swapId) =
      match statuses.TryRemove(swapId) with
      | true, _ ->
        ()
      | _ ->
        logger.LogError($"Failed to stop listening to {swapId}. This should never happen")

