namespace NLoop.Server.Projections

open System
open System.Net
open System.Threading
open FSharp.Control.Tasks
open FsToolkit.ErrorHandling
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
type SwapStateProjection(loggerFactory: ILoggerFactory,
                  opts: IOptions<NLoopOptions>,
                  checkpointDB: ICheckpointDB,
                  actor: SwapActor,
                  eventAggregator: IEventAggregator) as this =
  inherit BackgroundService()
  let log = loggerFactory.CreateLogger<SwapStateProjection>()

  let mutable _state: Map<StreamId, Swap.State> = Map.empty
  let lockObj = obj()

  let handleEvent (eventAggregator: IEventAggregator) : EventHandler =
    fun (event) -> unitTask {
      match event.ToRecordedEvent(Swap.serializer) with
      | Error _ -> ()
      | Ok r when not <| r.StreamId.Value.StartsWith(Swap.entityType) -> ()
      | Ok r ->
        this.State <-
          (
            if this.State |> Map.containsKey r.StreamId |> not then
              this.State |> Map.add r.StreamId actor.Aggregate.Zero
            else
              this.State
          )
          |> Map.change
            (r.StreamId)
            (Option.map(fun s -> actor.Aggregate.Apply s r.Data))
        log.LogTrace($"Publishing RecordedEvent {r}")
        eventAggregator.Publish<RecordedEvent<Swap.Event>> r
        do!
          r.EventNumber.Value
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
    with get(): Map<_,_> = _state
    and set v =
      lock (lockObj) <| fun () ->
        _state <- v

  override this.ExecuteAsync(stoppingToken) = unitTask {
    let maybeCheckpoint = ValueNone
    //let! maybeCheckpoint =
      //checkpointDB.GetSwapStateCheckpoint(stoppingToken)
    let checkpoint =
      maybeCheckpoint
      |> ValueOption.map(Checkpoint.StreamPosition)
      |> ValueOption.defaultValue Checkpoint.StreamStart
    log.LogDebug($"Start projecting from checkpoint {checkpoint}")
    try
      do! subscription.SubscribeAsync(checkpoint, stoppingToken)
    with
    | ex ->
      log.LogError($"{ex}")
  }
