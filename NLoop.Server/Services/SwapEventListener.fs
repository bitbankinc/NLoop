namespace NLoop.Server.Services


open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open System.Threading.Channels
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NLoop.Domain
open NLoop.Server
open NLoop.Server.Actors

type ISwapEventListener =
  abstract member RegisterSwap: id: string  * network: Network -> unit

type SwapEventListener(boltzClient: BoltzClient,
                       logger: ILogger<SwapEventListener>,
                       actor: SwapActor
                       ) =
  inherit BackgroundService()

  let tasks = ConcurrentBag()

  override this.ExecuteAsync(stoppingToken) =
    unitTask {
        try
          let! boltzVersion = boltzClient.GetVersionAsync()
          logger.LogInformation($"Listening to boltz version {boltzVersion.Version}")
          ()
        with
        | ex ->
          logger.LogCritical($"Connection to Boltz server {boltzClient.HttpClient.BaseAddress} failed!")
          logger.LogError($"{ex.Message}")
          raise <| ex

        while not <| stoppingToken.IsCancellationRequested do
          let! t = Task.WhenAny(tasks)
          do! t
          ()
    }
  member private this.HandleSwapUpdate(swapStatus, id, network) = unitTask {
    let cmd = { Swap.Data.SwapStatusUpdate.Response = swapStatus
                Swap.Data.SwapStatusUpdate.Network = network }
    do! actor.Execute(id, Swap.Msg.SwapUpdate(cmd))
  }

  interface ISwapEventListener with
    member this.RegisterSwap(id: string, network) =
      let a = async {
          let! first = boltzClient.GetSwapStatusAsync(id) |> Async.AwaitTask
          do! this.HandleSwapUpdate(first.ToDomain, id |> SwapId, network) |> Async.AwaitTask

          while true do
            do! Async.Sleep 1000
            let! first = boltzClient.GetSwapStatusAsync(id) |> Async.AwaitTask
            do! this.HandleSwapUpdate(first.ToDomain, id |> SwapId, network) |> Async.AwaitTask


          (*
          for a in boltzClient.StartListenToSwapStatusChange(id) do
            do! this.HandleSwapUpdate(a.ToDomain, id, network) |> Async.AwaitTask
          *)
        }
      tasks.Add(a |> Async.StartAsTask)
