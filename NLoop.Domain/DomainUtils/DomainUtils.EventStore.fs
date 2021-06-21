namespace NLoop.Domain.Utils

open System
open EventStore.ClientAPI
open NLoop.Domain
open FsToolkit.ErrorHandling

module EventStore =

  type EventStoreConfig = {
    Uri: Uri
  }
  open FSharp.Control.Tasks

  type SerializedRecordedEvent with
    static member FromEventStoreResolvedEvent(resolvedEvent: ResolvedEvent) = result {
      let! eventNumber =
        resolvedEvent.Event.EventNumber
        |> EventNumber.Create
        |> Result.mapError (StoreError)
      return
        {
          SerializedRecordedEvent.Id =
            resolvedEvent.Event.EventId |> EventId.EventId
          Type =
            resolvedEvent.Event.EventType
            |> EventType.EventType
          EventNumber = eventNumber
          CreatedDate =
            resolvedEvent.Event.Created
          Data =
            resolvedEvent.Event.Data
          Meta =
            resolvedEvent.Event.Metadata
        }
    }

  let readLast (conn: IEventStoreConnection) =
    fun (streamId: StreamId) -> task {
      let endOfStream = StreamPosition.End |> int64
      try
        let! r = conn.ReadEventAsync(streamId.Value, endOfStream, false)
        return
          match r.Event |> Option.ofNullable with
          | Some resolvedEvent ->
            resolvedEvent
            |> SerializedRecordedEvent.FromEventStoreResolvedEvent
            |> Result.map Some
          | None -> None |> Ok
      with ex ->
        return
          $"Error reading events from EventStoreDB\n%A{ex}"
          |> StoreError
          |> Error
    }

  let readStream (conn: IEventStoreConnection) =
    fun (streamId: StreamId) -> taskResult {
      let mutable currentSlice: StreamEventsSlice = null
      let mutable nextSliceStart = StreamPosition.Start |> int64
      let arr = ResizeArray<_>()
      try
        while (currentSlice |> isNull || currentSlice.IsEndOfStream |> not) do
          let! r = conn.ReadStreamEventsForwardAsync(streamId.Value, nextSliceStart, 200, false)
          currentSlice <- r
          nextSliceStart <- currentSlice.NextEventNumber
          let! serializedEvents =
            currentSlice.Events
            |> Seq.map SerializedRecordedEvent.FromEventStoreResolvedEvent
            |> Seq.toList
            |> List.sequenceResultM
          arr.AddRange(serializedEvents)
        return arr :> seq<_>
      with ex ->
        return! $"Error reading stream with id {streamId}! \n%A{ex}" |> StoreError |> Error
    }

  type ExpectedVersionUnion with
    member this.AsEventStoreExpectedVersion =
      match this with
      | Any -> ExpectedVersion.Any |> int64
      | NoStream -> ExpectedVersion.NoStream |> int64
      | StreamExists -> ExpectedVersion.StreamExists |> int64
      | Specific v -> v

  type SerializedEvent with
    member this.AsEventData =
      let guid = Guid.NewGuid()
      EventData(guid, this.Type.Value, true, this.Data, this.Meta)

  let writeStream (conn: IEventStoreConnection) =
    fun (expectedVersion: ExpectedVersionUnion) (events: SerializedEvent list) (streamId: StreamId) -> task {
      let! _writeResult =
        events
        |> List.map(fun e -> e.AsEventData)
        |> List.toArray
        |> fun e -> conn.AppendToStreamAsync(streamId.Value, expectedVersion.AsEventStoreExpectedVersion, e)
      return Ok()
    }
  let eventStore
    (uri: Uri)
    : Store =
      let conn =
        let connSettings = ConnectionSettings.Create().DisableTls().Build()
        EventStoreConnection.Create(connSettings, uri)
      conn.ConnectAsync().Wait()
      {
        ReadLast =  readLast conn
        ReadStream = readStream conn
        WriteStream = writeStream conn
      }

