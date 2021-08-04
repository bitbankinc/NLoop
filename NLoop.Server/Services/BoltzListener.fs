namespace NLoop.Server.Services


open System
open System.Collections.Concurrent
open System.Threading.Tasks
open DotNetLightning.Utils
open FSharp.Control
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

open NLoop.Domain
open NLoop.Server
open NLoop.Server.Actors

type BoltzListener(boltzClient: BoltzClient,
                       logger: ILogger<BoltzListener>,
                       actor: SwapActor
                       ) =

  inherit BackgroundService()
  let statuses = ConcurrentDictionary<SwapId, SwapStatusType voption>()


  override  this.ExecuteAsync(ct) = unitTask {
      try
        // Just to check the connection on startup.
        let! _boltzVersion = boltzClient.GetVersionAsync(ct).ConfigureAwait(false)

        while not <| ct.IsCancellationRequested do
          do! Task.Delay 5000
          ct.ThrowIfCancellationRequested()
          for kv in statuses do
            let swapId = kv.Key
            let! resp = boltzClient.GetSwapStatusAsync(swapId.Value, ct).ConfigureAwait(false)
            let isNoChange = kv.Value.IsSome && resp.SwapStatus = kv.Value.Value
            if isNoChange then () else
            if not <| statuses.TryUpdate(swapId, (resp.SwapStatus |> ValueSome), kv.Value) then
              logger.LogWarning($"Failed to update ({swapId})! Probably already finished?")
            else
              do! actor.Execute(swapId, Swap.Command.SwapUpdate(resp.ToDomain))
      with
      | :? OperationCanceledException ->
        ()
      | ex ->
        logger.LogCritical($"Connection to Boltz server {boltzClient.HttpClient.BaseAddress} failed!")
        logger.LogError($"{ex.Message}")
        raise <| ex
    }

  interface ISwapEventListener with
    member this.RegisterSwap(swapId: SwapId) =
      statuses.TryAdd(swapId, ValueNone)
      |> ignore

    member this.RemoveSwap(swapId) =
      match statuses.TryRemove(swapId) with
      | true, _ ->
        ()
      | _ ->
        logger.LogError($"Failed to stop listening to {swapId}. This should never happen")

