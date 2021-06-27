namespace NLoop.Server.Projections

open System
open System.Net
open System.Threading
open EventStore.ClientAPI.Projections
open FSharp.Control.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.Utils
open NLoop.Domain.Utils.EventStore
open NLoop.Server
open NLoop.Server.Actors

/// Create Swap Projection with Catch-up subscription.
/// TODO: Use EventStoreDB's inherent Projection system
type SwapListener(loggerFactory: ILoggerFactory,
                  opts: IOptions<NLoopOptions>,
                  checkpointDB: ICheckpointDB,
                  actor: SwapActor,
                  eventAggregator: IEventAggregator) as this =
  inherit BackgroundService()
  let log = loggerFactory.CreateLogger<SwapListener>()

  let mutable _state: Map<StreamId, Swap.State> = Map.empty
  let lockObj = obj()

  let handleEvent (eventAggregator: IEventAggregator) : EventHandler =
    fun (event) -> unitTask {
      let eventR =
        event.ToRecordedEvent(Swap.serializer)
        |> Result.map(fun r ->
          this.State <-
            this.State
            |> Map.change
              (r.StreamId)
              (Option.map(fun s -> actor.Aggregate.Apply s r.Data))
          eventAggregator.Publish<RecordedEvent<Swap.Event>> r
          r
        )
      match eventR with
      | Error _err -> () // Not the event we are concerned.
      | Ok e ->
        do!
          e.EventNumber.Value
          |> int64
          |> fun n -> checkpointDB.SetSwapStateCheckpoint(n, CancellationToken.None)
    }

  let subscription =
    EventStoreDBSubscription(
      { EventStoreConfig.Uri = opts.Value.EventStoreUrl |> Uri },
      "swap listener",
      SubscriptionTarget.All,
      loggerFactory.CreateLogger(),
      handleEvent eventAggregator)
  member this.State
    with get () = _state
    and set v =
      lock (lockObj) <| fun () ->
        _state <- v

  override this.ExecuteAsync(stoppingToken) = unitTask {
    let! maybeCheckpoint = checkpointDB.GetSwapStateCheckpoint(stoppingToken)
    let checkpoint =
      maybeCheckpoint
      |> ValueOption.map(Checkpoint.StreamPosition)
      |> ValueOption.defaultValue Checkpoint.StreamStart
    do! subscription.SubscribeAsync(checkpoint, stoppingToken)
  }
