namespace NLoop.Server.Projections

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open EventStore.ClientAPI
open FSharp.Control.Tasks
open FsToolkit.ErrorHandling
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.Utils
open NLoop.Server

type StartHeight = BlockHeight
type IOnGoingSwapStateProjection =
  abstract member State: Map<StreamId, StartHeight * Swap.State>
  abstract member FinishCatchup: Task

/// Create Swap Projection with Catch-up subscription.
/// It will event RecordedEvent<Swap.Event> to EventAggregator.
/// But in catch-up process, it will hold emitting and emits only for those which has not yet completed
/// when the catchup finishes. This is for other services such as <see cref="SwapProcessManager" />
/// To make their own decision about doing side effects according to the latest swap state.
type OnGoingSwapStateProjection(loggerFactory: ILoggerFactory,
                  opts: IOptions<NLoopOptions>,
                  actor: ISwapActor,
                  eventAggregator: IEventAggregator,
                  conn: IEventStoreConnection) as this =
  inherit BackgroundService()
  let log = loggerFactory.CreateLogger<OnGoingSwapStateProjection>()
  let catchupCompletion = TaskCompletionSource()

  let mutable _state: Map<StreamId, StartHeight * Swap.State> = Map.empty
  let lockObj = obj()

  let ongoingEvents = ConcurrentBag()

  let handleEvent (eventAggregator: IEventAggregator) : EventHandler =
    fun event -> unitTask {
      try
        if not <| event.StreamId.Value.StartsWith(Swap.entityType) then () else
        match event.ToRecordedEvent(Swap.serializer) with
        | Error e ->
          log.LogCritical $"Failed to deserialize event {e}"
          ()
        | Ok r ->
          this.State <-
            (
              if this.State |> Map.containsKey r.StreamId |> not then
                this.State |> Map.add r.StreamId (BlockHeight(0u), actor.Aggregate.Zero)
              else
                this.State
            )
            |> Map.change
              r.StreamId
              (Option.map(fun (h, s) ->
                let nextState = actor.Aggregate.Apply s r.Data
                let startHeight =
                  match r.Data with
                  | Swap.Event.NewLoopOutAdded { Height = h }
                  | Swap.Event.NewLoopInAdded { Height = h } -> h
                  | _ -> h
                (startHeight, nextState)
              ))
            // We don't hold finished swaps on-memory for the scalability.
            |> Map.filter(fun _ (_, state) -> match state with | Swap.State.Finished _ -> false | _ -> true)

          if catchupCompletion.Task.IsCompleted then
            log.LogInformation($"Publishing RecordedEvent {r.Data.Type} for {r.StreamId}")
            log.LogTrace($"Publishing RecordedEvent {r}")
            eventAggregator.Publish<RecordedEventPub<Swap.Event>> { IsCatchUp = false; RecordedEvent = r }
            eventAggregator.Publish<RecordedEvent<Swap.Event>> r
          else
            ongoingEvents.Add r
        with
        | ex -> log.LogCritical $"{ex}"
    }

  let onFinishCatchup _ =
    for r in ongoingEvents |> Seq.rev do
      log.LogInformation($"Catchup: Publishing RecordedEvent {r.Data.Type} for {r.StreamId}")
      log.LogTrace($"Catchup: Publishing RecordedEvent {r}")
      eventAggregator.Publish<RecordedEventPub<Swap.Event>> { IsCatchUp = true; RecordedEvent = r }
    ongoingEvents.Clear()
    log.LogDebug "Catchup completed"
    catchupCompletion.SetResult()
    ()

  let subscription =
    EventStoreDBSubscription(
      { EventStoreConfig.Uri = opts.Value.EventStoreUrl |> Uri },
      nameof(OnGoingSwapStateProjection),
      SubscriptionTarget.All,
      loggerFactory.CreateLogger(),
      handleEvent eventAggregator,
      onFinishCatchup,
      conn)

  member this.State
    with get(): Map<_, StartHeight * Swap.State> = _state
    and set v =
      lock lockObj <| fun () ->
        _state <- v

  interface IOnGoingSwapStateProjection with
    member this.State = this.State
    member this.FinishCatchup = catchupCompletion.Task

  override this.ExecuteAsync(stoppingToken) = unitTask {
    let checkpoint =
      Checkpoint.StreamStart
    log.LogInformation $"Starting {nameof(OnGoingSwapStateProjection)} from checkpoint {checkpoint}..."
    try
      do! subscription.SubscribeAsync(checkpoint, stoppingToken)
    with
    | ex ->
      log.LogError($"{ex}")
  }


[<AutoOpen>]
module ISwapStateProjectionExtensions =
  type IOnGoingSwapStateProjection with
    member this.OngoingLoopIns =
      this.State
      |> Seq.choose(fun kv ->
                    let _, v = kv.Value
                    match v with
                    | Swap.State.In (_height, o) -> Some o
                    | _ -> None
                    )

    member this.OngoingLoopOuts =
      this.State
      |> Seq.choose(fun kv ->
                    let _, v = kv.Value
                    match v with
                    | Swap.State.Out (_height, o) -> Some o
                    | _ -> None
                    )
