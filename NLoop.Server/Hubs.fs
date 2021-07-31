namespace NLoop.Server

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open Microsoft.AspNetCore.SignalR
open NLoop.Domain
open FSharp.Control.Tasks.Affine
open FSharp.Control.Reactive
open NLoop.Server.Actors

type IEventClient =
  abstract member HandleSwapEvent: Swap.EventWithId -> Task

type EventHub(eventAggregator: IEventAggregator) =
  inherit Hub()

  let mutable subscription = null

  member this.ListenSwapEvents([<EnumeratorCancellation>] ct: CancellationToken): IAsyncEnumerable<Swap.EventWithId> =
    let channel =
      let opts = BoundedChannelOptions(2)
      opts
      |> Channel.CreateBounded<Swap.EventWithId>
    subscription <-
      eventAggregator.GetObservable<Swap.EventWithId>()
      |> Observable.flatmapTask(fun e ->
        task {
          let! shouldContinue = channel.Writer.WaitToWriteAsync(ct)
          if shouldContinue then
            do! channel.Writer.WriteAsync(e, ct)
          else
            raise <| HubException($"Channel Stopped")
        }
      )
      |> Observable.subscribe(id)
    channel.Reader.ReadAllAsync()

  interface IDisposable with
    override this.Dispose() =
      subscription
      |> Option.ofObj
      |> Option.iter(fun s ->
        s.Dispose()
      )
