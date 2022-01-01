namespace NLoop.Server.Projections

open System
open DotNetLightning.Utils
open EventStore.ClientAPI
open FSharp.Control.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.Utils
open NLoop.Domain.Utils.EventStore
open NLoop.Server

type IRecentSwapFailureProjection =
  abstract member FailedLoopOuts: Map<ShortChannelId, DateTimeOffset>
  abstract member FailedLoopIns: Map<NodeId, DateTimeOffset>

/// Cache recently failed swaps on memory
/// Used in AutoLoop to backoff the failure
type RecentSwapFailureProjection(opts: IOptions<NLoopOptions>,
                                 loggerFactory: ILoggerFactory,
                                 conn: IEventStoreConnection) as this =
  inherit BackgroundService()

  /// We have no big reason to choose this number.
  /// This is big enough for `AutoLoopManager` to backoff the swap suggestion.
  /// and small enough to not pressure the memory usage.
  let [<Literal>] InMemoryHistorySize = 255
  let log = loggerFactory.CreateLogger<RecentSwapFailureProjection>()

  let dropOldest(m: Map<_, struct(_ * UnixDateTime voption)>) =
    let oldestId, _ =
      let dummy = StreamId.Create Swap.entityType "dummy"
      let folder acc k struct (_, date) =
        match date with
        | ValueNone -> acc
        | ValueSome newDate ->
          /// oldest date is the smallest date.
          let _, currentOldestDate = acc
          if currentOldestDate < newDate then acc else (k, newDate)
      this.FailedLoopOutSwapState
      |> Map.fold folder (dummy, UnixDateTime.UtcNow)
    Map.remove oldestId m

  let handleEvent (re: SerializedRecordedEvent) = unitTask {
    if not <| re.StreamId.Value.StartsWith(Swap.entityType) then () else
    match re.ToRecordedEvent(Swap.serializer) with
    | Error _ -> ()
    | Ok r ->
      match r.Data with
      | Swap.Event.NewLoopOutAdded(_h, o) ->
        this.FailedLoopOutSwapState <-
          this.FailedLoopOutSwapState
          |> Map.add re.StreamId (o.OutgoingChanIds, ValueNone)

        // We only need information about the recent swap failure.
        // discard old ones so that we don't waste too much memory.
        if this.FailedLoopOutSwapState.Count > InMemoryHistorySize then
          this.FailedLoopOutSwapState <-
            this.FailedLoopOutSwapState
            |> dropOldest
      | Swap.Event.NewLoopInAdded(_h, i) ->
        i.LastHop
        |> Option.iter(fun lastHop ->
          this.FailedLoopInSwapState <-
            this.FailedLoopInSwapState
              |> Map.add re.StreamId (NodeId lastHop, ValueNone)
        )

        if this.FailedLoopInSwapState.Count > InMemoryHistorySize then
          this.FailedLoopInSwapState <-
            this.FailedLoopInSwapState
            |> dropOldest
      | Swap.Event.FinishedByError _
      | Swap.Event.FinishedByTimeout _
      | Swap.Event.FinishedByRefund _ ->
        this.FailedLoopOutSwapState <-
          this.FailedLoopOutSwapState
          |> Map.change
              re.StreamId
              (Option.map(fun struct (chanId, _) -> (chanId, ValueSome re.CreatedDate)))
        this.FailedLoopInSwapState <-
          this.FailedLoopInSwapState
          |> Map.change
              re.StreamId
              (Option.map(fun struct (nodeId, _) -> (nodeId, ValueSome re.CreatedDate)))
      | _ -> ()
  }
  let subscription =
    EventStoreDBSubscription(
      { EventStoreConfig.Uri = opts.Value.EventStoreUrl |> Uri },
      nameof(RecentSwapFailureProjection),
      SubscriptionTarget.All,
      loggerFactory.CreateLogger(),
      handleEvent,
      (fun _ -> ()),
      conn
    )

  let mutable failedLoopOutSwapState = Map.empty<_, struct(ShortChannelId[] *  UnixDateTime voption)>
  let mutable failedLoopInSwapState = Map.empty<_, struct(NodeId * UnixDateTime voption)>
  let _swapStateLockObjOut = obj()
  let _swapStateLockObjIn = obj()
  member this.FailedLoopOutSwapState
    with private get(): Map<_, struct(ShortChannelId[] * _)> = failedLoopOutSwapState
    and private set v =
      lock _swapStateLockObjOut (fun () -> failedLoopOutSwapState <- v)

  member this.FailedLoopInSwapState
    with private get(): Map<_, struct(_ * _)> = failedLoopInSwapState
    and private set v =
      lock _swapStateLockObjIn (fun () -> failedLoopInSwapState <- v)

  interface IRecentSwapFailureProjection with
    member this.FailedLoopIns =
      this.FailedLoopInSwapState
      |> Map.toSeq
      |> Seq.choose(fun (_, struct(cIds, date)) -> match date with | ValueNone -> None | ValueSome d -> Some (cIds, DateTimeOffset(d.Value)) )
      |> Map.ofSeq

    member this.FailedLoopOuts =
      let entries =
        this.FailedLoopOutSwapState
        |> Map.toSeq
        |> Seq.choose(fun (_, struct(cIds, date)) -> match date with | ValueNone -> None | ValueSome d -> Some (cIds, DateTimeOffset(d.Value)))
      // flatten the channel ids
      seq [
        for k, v in entries do
          for chanId in k do
            chanId, v
      ]
      |> Map.ofSeq
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
