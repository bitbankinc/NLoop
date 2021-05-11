namespace NLoop.Server

open System
open System.Collections.Generic
open System.Threading.Tasks
open System.Threading.Channels
open Microsoft.AspNetCore.SignalR
open NLoop.Domain
open FSharp.Control.Tasks.Affine
open FSharp.Control.Reactive

type IEventClient =
  abstract member HandleSwapEvent: Swap.Event -> Task

type EventHub(eventAggregator: IEventAggregator) =
  inherit Hub<IEventClient>()

  let mutable subscription = None

  override this.OnConnectedAsync() =
    let publish (e: Swap.Event) = unitTask {
        do! this.Clients.All.HandleSwapEvent(e)
      }

    subscription <-
      eventAggregator.GetObservable<Swap.Event>()
      |> Observable.subscribe(publish >> ignore)
      |> Some
    Task.CompletedTask

  member this.ListenSwapEvents(): ChannelReader<Swap.Event> =
    let channel =
      let opts = BoundedChannelOptions(2)
      opts
      |> Channel.CreateBounded<Swap.Event>
    let s =
      eventAggregator.GetObservable<Swap.Event>()
      |> Observable.subscribe(fun e ->
        let t = unitTask {
          let! shouldContinue = channel.Writer.WaitToWriteAsync()
          if shouldContinue then
            do! channel.Writer.WriteAsync(e)
          else
            raise <| HubException($"Channel Stopped")
         }
        ()
      )
    channel.Reader

  interface IDisposable with
    override this.Dispose() =
      subscription
      |> Option.iter(fun s ->
        s.Dispose()
      )
