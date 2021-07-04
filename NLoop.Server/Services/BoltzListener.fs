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

  let tasks = ConcurrentDictionary<SwapId, Task>()

  interface IHostedService with
    override this.StartAsync(stoppingToken) =
      unitTask {
          try
            let! boltzVersion = boltzClient.GetVersionAsync(stoppingToken)
            logger.LogInformation($"Listening to boltz version {boltzVersion.Version}")
            ()
          with
          | ex ->
            logger.LogCritical($"Connection to Boltz server {boltzClient.HttpClient.BaseAddress} failed!")
            logger.LogError($"{ex.Message}")
            raise <| ex
      }

    member this.StopAsync(_cancellationToken) =
      tasks.Values |> Seq.iter(fun s -> s.Dispose())
      tasks.Clear()
      Task.CompletedTask

  member private this.HandleSwapUpdate(swapStatus, id) = unitTask {
    do! actor.Execute(id, Swap.Command.SwapUpdate(swapStatus))
  }

  interface ISwapEventListener with
    member this.RegisterSwap(swapId: SwapId) =
      let t = task {
          while true do
            do! Async.Sleep 5000
            let! first = boltzClient.GetSwapStatusAsync(swapId.Value)
            do! this.HandleSwapUpdate(first.ToDomain, swapId)
        }
      tasks.TryAdd(swapId, t)
      |> ignore

    member this.RemoveSwap(swapId) =
      match tasks.TryRemove(swapId) with
      | true, t ->
        t.Dispose()
      | _ ->
        logger.LogError($"Failed to stop listening to {swapId}. This should never happen")

