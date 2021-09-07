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

module Data =
  type ListChannelResponse = {
    Id: ShortChannelId
    Cap: Money
    LocalBalance: Money
    NodeId: PubKey
  }

type State = {
  Rules: AutoLoopRule option
  OngoingSwap: SwapId option
}
  with
  static member Zero = {
    Rules = None
    OngoingSwap = None
  }

type Command =
  | SetRule of rule: AutoLoopRule

[<Literal>]
let entityType = "autoloop"

type Event =
  | NewRuleAdded of rule: AutoLoopRule
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
  GetAllChannelIds: unit -> Task<Data.ListChannelResponse list>
  DispatchLoopOut: LoopOut -> Task
  DispatchLoopIn: LoopIn -> Task
}

type Error =
  | ChannelDoesNotExist
  | BogusRule of string

let private enhanceEvents date source (events: Event list) =
  events |> List.map(fun e -> e.ToEventSourcingEvent date source)

let executeCommand (deps: Deps) (_s: State) (cmd: ESCommand<Command>): Task<Result<ESEvent<Event> list, _>> =
  let { CommandMeta.EffectiveDate = effectiveDate; Source = source } = cmd.Meta
  let enhance = enhanceEvents effectiveDate source
  taskResult {
    match cmd.Data with
    | SetRule({ Channel  = channel; IncomingThreshold = inThreshold; OutgoingThreshold = outThreshold }) ->
      let! channels = deps.GetAllChannelIds()
      let maybeNewRule =
        channels
        |> Seq.tryPick(fun c ->
          if c.Id = channel then
            Some (c, {
              AutoLoopRule.Channel = c.Id
              IncomingThreshold = inThreshold
              OutgoingThreshold =  outThreshold
            })
          else
            None)
      match maybeNewRule with
      | Some (c, rule) ->
        let validateRuleIsCompatibleToChannel (c: Data.ListChannelResponse) (rule: AutoLoopRule) =
          let middle = (c.Cap / 2L)
          if rule.IncomingThreshold >= middle then
            $"IncomingThreshold ({rule.IncomingThreshold}) must be smaller than the half of the channel cap ({c.Cap})."
            |> BogusRule |> Error
          elif rule.OutgoingThreshold <= middle then
            $"OutgoingThreshold ({rule.OutgoingThreshold}) must be larger than the half of the channel cap ({c.Cap})."
            |> BogusRule |> Error
          else
            Ok()
        do! validateRuleIsCompatibleToChannel c rule
        return [Event.NewRuleAdded(rule)] |> enhance
      | _ ->
        return! ChannelDoesNotExist |> Error
  }

let applyChanges(state: State) (event: Event) =
  match event with
  | NewRuleAdded(rule) ->
    { state with Rules = Some rule }
  | UnknownTagEvent _ ->
    state

type Aggregate = Aggregate<State, Command, Event, Error, uint16 * DateTime>

type EntityId = ShortChannelId
type Handler = Handler<State, Command, Event, Error, EntityId>
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
  Id: EntityId
  Event: Event
}

type ErrorWithId = {
  Id: EntityId
  Error: EventSourcingError<Error>
}

let getHandler aggr eventStoreUri =
  getRepository eventStoreUri
  |> Handler.Create aggr
