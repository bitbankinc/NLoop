namespace NLoop.Server.Services

open System
open System.Collections.Generic
open FSharp.Control.Tasks

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open NLoop.Server


type SwapEventListeners(
                        listeners: ISwapEventListener seq,
                        logger: ILogger<SwapEventListeners>) =

  interface IHostedService with
    member this.StartAsync(cancellationToken) = unitTask {
      for l in listeners do
        logger.LogDebug($"starting swap event listener {l.GetType().Name}")
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
