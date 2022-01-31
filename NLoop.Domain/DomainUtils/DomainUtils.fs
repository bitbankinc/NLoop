namespace NLoop.Domain.Utils

open System.IO
open DotNetLightning.Serialization
open DotNetLightning.Utils
open System
open FSharp.Control
open NLoop.Domain
open System.Threading.Tasks
open FsToolkit.ErrorHandling

/// Friendly name of the domain event type. Might be used later to query the event stream.
type EventType = EventType of string
  with
  member this.Value = let (EventType v) = this in v
  member this.ToBytes() =
    this.Value |> System.Text.Encoding.UTF8.GetBytes

  static member FromBytes(b: byte[]) =
    System.Text.Encoding.UTF8.GetString b
    |> EventType

type StreamId = StreamId of string

  with
  member this.Value = let (StreamId v) = this in v
  static member Create<'TEntityId> (entityType: string) (entityId: 'TEntityId) =
    $"{entityType}-{entityId.ToString().ToLower()}"
    |> StreamId

type EventId = EventId of Guid

[<Struct>]
type EventNumber = private EventNumber of uint64
  with
  member this.Value = let (EventNumber v) = this in v
  static member Create(i: uint64) =
    EventNumber i

  static member Create(i: int64) =
    if (i < 0L) then Error $"Negative event number %i{i}" else
    EventNumber(uint64 i)
    |> Ok


[<Struct>]
type UnixDateTime = private UnixDateTime of DateTime
  with
  member this.Value = let (UnixDateTime d) = this in d
  static member UtcNow = DateTime.UtcNow |> UnixDateTime
  static member Create(unixTime: uint64) =
    try
      NBitcoin.Utils.UnixTimeToDateTime(unixTime).UtcDateTime
      |> UnixDateTime
      |> Ok
    with
    | ex ->
      Error($"UnixDateTime Creation error %A{ex}")
  static member Create(dateTime: DateTime) =
    try
      DateTimeOffset(dateTime, TimeSpan.Zero)
      |> NBitcoin.Utils.DateTimeToUnixTime
      |> uint64
      |> UnixDateTime.Create
    with
    | ex ->
      Error($"UnixDateTime Creation error %A{ex}")

/// `AsOf` means as it was or will be on and after that date.
/// `AsAt` means as it is at that particular time only. It implies there may be changes.
/// `Latest` means as it currently is. Specifically, include all events in the stream.
///
/// We only use `AsAt` in the Handler. But we might want to use `AsOf` for the audit purpose.
[<Struct>]
type ObservationDate =
  | Latest
  | AsOf of asof: UnixDateTime
  | AsAt of asat: UnixDateTime

type StoreError = StoreError of string
type EventSourcingError<'T> =
  | Store of StoreError
  | DomainError of 'T

type EventMeta = {
  /// Date at which event is effective in the domain
  EffectiveDate: UnixDateTime
  /// Origin of this event.
  SourceName: string
}
  with
  member this.ToBytes() =
    let d =
      this.EffectiveDate.Value
      |> fun x -> DateTimeOffset(x, TimeSpan.Zero)
      |> NBitcoin.Utils.DateTimeToUnixTime
      |> fun u ->  NBitcoin.Utils.ToBytes(u, false).BytesWithLength()
    let source =
      this.SourceName
      |> System.Text.Encoding.UTF8.GetBytes
      |> fun b -> b.BytesWithLength()
    Array.concat [d; source]

  static member FromBytes(b: byte[]) =
    result {
      try
        let effectiveDateB, b = b.PopWithLen()
        let sourceName, _b = b.PopWithLen()
        let effectiveDate =
          effectiveDateB
          |> fun b -> NBitcoin.Utils.ToUInt32(b, false)
          |>  NBitcoin.Utils.UnixTimeToDateTime
          |> fun dateTimeOffset -> dateTimeOffset.UtcDateTime
        let! d = UnixDateTime.Create(effectiveDate)
        return
          {
            EffectiveDate = d
            SourceName =
              sourceName
              |> System.Text.Encoding.UTF8.GetString
          }
      with
      | ex ->
        return! Error (sprintf "Failed to Deserialize EventMeta %A" ex)
    }

type Serializer<'TEvent> = {
  EventToBytes: 'TEvent -> byte[]
  BytesToEvents: byte[] -> Result<'TEvent, string>
}

type ESEvent<'TEvent> = {
  Type: EventType
  Data: 'TEvent
  Meta: EventMeta
}
  with
  member this.ToSerializedEvent (serializer: Serializer<'TEvent>) =
    {
      SerializedEvent.Data = this.Data |> serializer.EventToBytes
      Type = this.Type
      Meta = this.Meta.ToBytes()
    }

and SerializedEvent = {
  Type: EventType
  Data: byte[]
  Meta: byte[]
}
  with
  member this.Serialize(ls: LightningWriterStream) =
    ls.WriteWithLen(this.Type.ToBytes())
    ls.WriteWithLen(this.Data)
    ls.WriteWithLen(this.Meta)

  static member Deserialize(ls: LightningReaderStream) =
    try
      {
        Type = ls.ReadWithLen() |> EventType.FromBytes
        Data = ls.ReadWithLen()
        Meta = ls.ReadWithLen()
      }
      |> Ok
    with
    | ex ->
      Error($"Failed to Deserialize SerializedEvent %A{ex}")

  member this.ToBytes() =
    use ms = new MemoryStream()
    use ls = new LightningWriterStream(ms)
    this.Serialize(ls)
    ms.ToArray()

  static member FromBytes(b: byte[]) =
    use ms = new MemoryStream(b)
    use ls = new LightningReaderStream(ms)
    SerializedEvent.Deserialize(ls)

type RecordedEvent<'TEvent> = {
  Id: EventId
  Type: EventType
  StreamId: StreamId
  EventNumber: EventNumber
  CreatedDate: UnixDateTime
  Data: 'TEvent
  Meta: EventMeta
}
  with
  member this.AsEvent =
    {
      ESEvent.Data = this.Data
      Type = this.Type
      Meta = this.Meta
    }


type SerializedRecordedEvent = {
  Id: EventId
  Type: EventType
  StreamId: StreamId
  EventNumber: EventNumber
  CreatedDate: UnixDateTime
  Data: byte[]
  Meta: byte[]
}
  with
  member this.ToRecordedEvent<'TEvent>(serializer: Serializer<'TEvent>) = result {
    let! e = this.Data |> serializer.BytesToEvents
    let! m = this.Meta |> EventMeta.FromBytes
    return
      {
        RecordedEvent.Id = this.Id
        Type = this.Type
        StreamId = this.StreamId
        EventNumber = this.EventNumber
        CreatedDate = this.CreatedDate
        Data = e
        Meta = m
      }
  }


type CommandMeta =
    { EffectiveDate: UnixDateTime
      Source: string }

type ESCommand<'DomainCommand> =
    { Data: 'DomainCommand
      Meta: CommandMeta }

type Aggregate<'TState, 'TCommand, 'TEvent, 'TError,'T when 'T : comparison> = {
  Zero: 'TState
  Filter: RecordedEvent<'TEvent> list -> RecordedEvent<'TEvent> list
  Enrich: ESEvent<'TEvent> list -> ESEvent<'TEvent> list
  Apply: 'TState -> 'TEvent -> 'TState
  Exec: 'TState -> ESCommand<'TCommand> -> Task<Result<ESEvent<'TEvent> list, 'TError>>
}

/// Currently we use only `NoStream` and `Specific`, and don't consider about concurrency check.
/// This means if someone tries to dispatch more than 1 `Command`s to a same entity(stream) at once,
/// It may throw `WrongExpectedVersionException`, and abort the later one.
/// This means every `Aggregate.Exec` operation is guaranteed to be atomic, but it may cause a problem.
/// i.e. the retry logic must be taken care when the execution is critical for the security or business logic.
///
/// See also: https://developers.eventstore.com/clients/dotnet/20.10/appending/optimistic-concurrency-and-idempotence.html
type ExpectedVersionUnion =
  | Any
  | NoStream
  | StreamExists
  | Specific of int64
  with
  static member FromLastEvent(maybeLastEvent: SerializedRecordedEvent option) =
    match maybeLastEvent with
    | Some lastEvent ->
      lastEvent.EventNumber.Value
      |> int64
      |> Specific
    | None ->
      NoStream

type Store = {
  ReadLast: StreamId -> Task<Result<SerializedRecordedEvent option, StoreError>>
  ReadStream: StreamId -> Task<Result<SerializedRecordedEvent seq, StoreError>>
  WriteStream: ExpectedVersionUnion -> SerializedEvent list -> StreamId -> Task<Result<unit, StoreError>>
}

type Repository<'TEvent, 'TEntityId> = {
  Version: 'TEntityId -> Task<Result<ExpectedVersionUnion, StoreError>>
  Load: 'TEntityId -> Task<Result<RecordedEvent<'TEvent> list, StoreError>>
  Commit: 'TEntityId -> ExpectedVersionUnion -> ESEvent<'TEvent> list -> Task<Result<unit, StoreError>>
}
  with
  static member Create(store: Store) (serializer: Serializer<'TEvent>)(entityType: string): Repository<'TEvent,'TEntityId> =
    let version (entityId: 'TEntityId) = taskResult {
      let! maybeLastEvent =
        entityId
        |> StreamId.Create entityType
        |> store.ReadLast
      return
        maybeLastEvent |> ExpectedVersionUnion.FromLastEvent
    }
    let load entityId = taskResult {
      let! serializedRecordedEvents =
        entityId
        |> StreamId.Create entityType
        |> store.ReadStream
      return!
        serializedRecordedEvents
        |> Seq.map(fun e -> e.ToRecordedEvent serializer)
        |> Seq.toList
        |> List.sequenceResultM
        |> Result.mapError(StoreError)
    }
    let commit (entityId: 'TEntityId) expectedVersion (events: ESEvent<'TEvent> list) =
      let streamId =
        entityId
        |> StreamId.Create entityType
      let serializedEvents =
        events
        |> List.map(fun e -> e.ToSerializedEvent serializer)
      store.WriteStream expectedVersion serializedEvents streamId

    {
      Version = version
      Load = load
      Commit = commit
    }

type Handler<'TState, 'TCommand, 'TEvent, 'TError, 'TEntityId> = {
  Replay: 'TEntityId -> ObservationDate -> Task<Result<ESEvent<'TEvent> list, EventSourcingError<'TError>>>
  Reconstitute: ESEvent<'TEvent> list -> 'TState
  Execute: 'TEntityId -> ESCommand<'TCommand> -> Task<Result<ESEvent<'TEvent> list, EventSourcingError<'TError>>>
}
  with
  static member Create
    (aggregate: Aggregate<'TState, 'TCommand, 'TEvent, 'TError, 'T>)
    (repo: Repository<'TEvent, 'TEntityId>) =
    let replay (entityId: 'TEntityId) (observationDate: ObservationDate) = taskResult {
      let! recordedEvents =
        repo.Load entityId
        |> TaskResult.mapError(EventSourcingError.Store)

      let onOrBeforeObservationDate
        { RecordedEvent.CreatedDate = createdDate; Meta = { EffectiveDate  = effectiveDate } } =
        match observationDate with
        | Latest -> true
        | AsOf d -> createdDate <= d
        | AsAt d -> effectiveDate <= d
      return
        recordedEvents
        |> aggregate.Filter
        |> List.filter(onOrBeforeObservationDate)
        |> List.map(fun rEvent -> rEvent.AsEvent)
        |> aggregate.Enrich
    }

    let reconstitute
      (events: ESEvent<'TEvent> list) =
      let folder acc (event: ESEvent<'TEvent>) =
        // we do not have to perform side effects when reconstituting the state
        let nextState = aggregate.Apply acc event.Data
        nextState
      events
      |> List.fold folder aggregate.Zero

    let rec execute
      (entityId: 'TEntityId)
      (command: ESCommand<'TCommand>) = taskResult {
        let { ESCommand.Meta = { EffectiveDate = date } } = command
        let! recordedEvents = replay entityId (AsAt date)
        let state = recordedEvents |> reconstitute
        let! newEvents =
          aggregate.Exec state command
          |> TaskResult.mapError(DomainError)
        let! expectedVersion =
          repo.Version entityId
          |> TaskResult.mapError EventSourcingError.Store
        do!
          repo.Commit entityId expectedVersion newEvents
          |> TaskResult.mapError(EventSourcingError.Store)
        return newEvents
      }
    {
      Replay = replay
      Reconstitute = reconstitute
      Execute = execute
    }

open System.Threading
type IActor<'TState, 'TCommand, 'TEvent, 'TError, 'TEntityId, 'T when 'T : comparison> =
  abstract member Handler: Handler<'TState, 'TCommand, 'TEvent, 'TError, 'TEntityId>
  abstract member Aggregate: Aggregate<'TState, 'TCommand, 'TEvent, 'TError, 'T>
  abstract member Execute:
    swapId: 'TEntityId *
    msg: 'TCommand *
    ?source: string
      -> Task

  // todo: use asyncSeq
  abstract member GetAllEntities: ?ct: CancellationToken -> Task<Result<Map<StreamId, 'TState>, StoreError>>

[<RequireQualifiedAccess>]
type Checkpoint =
  | StreamStart
  | StreamPosition of int64

type IDatabaseSubscription =
  abstract member SubscribeAsync: checkpoint: Checkpoint * ct: CancellationToken -> Task

[<RequireQualifiedAccess>]
type SubscriptionTarget =
  | All
  | SpecificStream of streamId: StreamId

type SubscriptionEventHandler = SerializedRecordedEvent -> Task
type SubscriptionParameter = {
  Owner: string
  Target: SubscriptionTarget
  HandleEvent: SubscriptionEventHandler
  OnFinishCatchUp: (obj -> unit) option
}
type GetDBSubscription = SubscriptionParameter -> IDatabaseSubscription
