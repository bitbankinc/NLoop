namespace NLoop.Server.Services

open FSharp.Control.Tasks
open Microsoft.Extensions.Hosting
open NLoop.Server


type SwapEventListeners(listeners: ISwapEventListener seq) =
  interface IHostedService with
    member this.StartAsync(cancellationToken) = unitTask {
      for l in listeners do
        match l with
        | :? IHostedService as s ->
          do! s.StartAsync(cancellationToken)
        | _ ->
          ()
    }
    member this.StopAsync(cancellationToken) = unitTask {
      for l in listeners do
        match l with
        | :? IHostedService as s ->
          do! s.StopAsync(cancellationToken)
        | _ ->
          ()
    }
