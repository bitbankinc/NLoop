[<RequireQualifiedAccess>]
module NLoop.Domain.AutoLoop

open System
open System.Text.Json
open System.Threading.Tasks
open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Domain.IO
open NLoop.Domain.Utils
open FsToolkit.ErrorHandling
open NLoop.Domain.Utils.EventStore

type State = {
  KnownChannels: ShortChannelId list
  Rules: Map<ShortChannelId, AutoLoopRule>
}
  with
  static member Zero = {
    KnownChannels = []
    Rules = Map.empty
  }

type Command =
  | SetRule of channelId: ShortChannelId * rule: AutoLoopRule

[<Literal>]
let entityType = "autoloop"

type Event =
  | NewRuleAdded of channelId: ShortChannelId * rule: AutoLoopRule
  | UnknownTagEvent of tag: uint16 * data: byte[]
  member this.EventTag =
    match this with
    | NewRuleAdded _ -> 0us
    | UnknownTagEvent(t, _) -> t
  member this.Type =
    match this with
    | NewRuleAdded _ -> "new_rule_added"
    | UnknownTagEvent(t, _) -> $"unknown_version_event_{t}"
  member this.ToEventSourcingEvent effectiveDate source : ESEvent<Event> =
    {
      ESEvent.Meta = { EventMeta.SourceName = source; EffectiveDate = effectiveDate }
      Type = (entityType + "-" + this.Type) |> EventType.EventType
      Data = this
    }
let private jsonConverterOpts =
  let o = JsonSerializerOptions()
  o.AddNLoopJsonConverters()
  o
let serializer : Serializer<Event> = {
  Serializer.EventToBytes = fun (e: Event) ->
    let v = e.EventTag |> fun t -> Utils.ToBytes(t, false)
    let b =
      match e with
      | UnknownTagEvent (_, b) ->
        b
      | e -> JsonSerializer.SerializeToUtf8Bytes(e, jsonConverterOpts)
    Array.concat (seq [v; b])
  BytesToEvents =
    fun b ->
      try
        let e =
          match Utils.ToUInt16(b.[0..1], false) with
          | 0us ->
            JsonSerializer.Deserialize(ReadOnlySpan<byte>.op_Implicit b.[2..], jsonConverterOpts)
          | v ->
            UnknownTagEvent(v, b.[2..])
        e |> Ok
      with
      | ex ->
        $"Failed to deserialize event json\n%A{ex}"
        |> Error
}

type Deps = {
  GetSwapParams: unit -> SwapParams
}

type Error = string

let executeCommand (_deps: Deps) (_s: State) (cmd: ESCommand<Command>): Task<Result<ESEvent<Event> list, _>> =
  taskResult {
    match cmd.Data with
    | SetRule(_id, _cmd) ->
      return failwith "todo"
  }

let applyChanges(state: State) (event: Event) =
  match event with
  | NewRuleAdded(_cId, _r) ->
    state
  | UnknownTagEvent _ ->
    state

type Aggregate = Aggregate<State, Command, Event, Error, uint16 * DateTime>
type Handler = Handler<State, Command, Event, Error, SwapId>
let getAggregate deps: Aggregate = {
  Zero = State.Zero
  Exec = executeCommand deps
  Aggregate.Apply = applyChanges
  Filter = id
  Enrich = id
  SortBy = fun event ->
    event.Data.EventTag, event.Meta.EffectiveDate.Value
}

let getRepository eventStoreUri =
  let store = eventStore eventStoreUri
  Repository.Create
    store
    serializer
    entityType

type EventWithId = {
  Id: SwapId
  Event: Event
}

type ErrorWithId = {
  Id: SwapId
  Error: EventSourcingError<Error>
}

let getHandler aggr eventStoreUri =
  getRepository eventStoreUri
  |> Handler.Create aggr
