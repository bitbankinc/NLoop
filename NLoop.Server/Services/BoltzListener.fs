namespace NLoop.Server.Services


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
        let! boltzVersion = boltzClient.GetVersionAsync(ct).ConfigureAwait(false)
        logger.LogInformation($"Listening to boltz version {boltzVersion.Version}")

        while true do
          logger.LogInformation($"Listening to Boltz...")
          for kv in statuses do
            logger.LogInformation($"Listening to Boltz for {kv.Key} in status {kv.Value}")
            do! Async.Sleep 5000
            let swapId = kv.Key
            let! resp = boltzClient.GetSwapStatusAsync(swapId.Value, ct).ConfigureAwait(false)
            logger.LogInformation($"resp was {resp.SwapStatus.AsString}")
            let isNoChange = kv.Value.IsSome && resp.SwapStatus = kv.Value.Value
            logger.LogInformation($"isNoChange? {isNoChange}")
            if (isNoChange) then () else
            if not <| statuses.TryUpdate(swapId, (resp.SwapStatus |> ValueSome), kv.Value) then
              logger.LogError($"Failed to update ({swapId})! this should never happen")
            else
              do! actor.Execute(swapId, Swap.Command.SwapUpdate(resp.ToDomain))
      with
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

