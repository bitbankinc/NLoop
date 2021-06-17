namespace NLoop.Domain.Utils

open System
open System.Threading
open System.Threading.Tasks
open EventStore.ClientAPI
open EventStore.ClientAPI
open NLoop.Domain.Utils.EventStore
open Microsoft.Extensions.Logging
open FSharp.Control.Tasks

type EventHandler = SerializedRecordedEvent -> Task

[<RequireQualifiedAccess>]
type SubscriptionStatus =
  | Subscribed
  | UnSubscribed

[<RequireQualifiedAccess>]
type Checkpoint =
  | StreamStart
  | StreamPosition of int64

type SubscriptionState = {
  Checkpoint: Checkpoint
  CancellationToken: CancellationToken
  SubscriptionStatus: SubscriptionStatus
}

type SubscriptionMessage =
  | Subscribe
  | Subscribed of EventStoreStreamCatchUpSubscription
  | Dropped of SubscriptionDropReason * exn
  | EventAppeared of Checkpoint
  | GetState of AsyncReplyChannel<SubscriptionState>

type SubscriptionMailbox = MailboxProcessor<SubscriptionMessage>

/// Based on https://github.com/ameier38/fsharp-eventstore-subscription
type EventStoreDBSubscription(eventStoreConfig: EventStoreConfig,
                              name: string,
                              streamId: StreamId,
                              log: ILogger<EventStoreDBSubscription>,
                              eventHandler: EventHandler) =

  let conn: IEventStoreConnection = EventStoreConnection.Create(eventStoreConfig.Uri)
  do conn.ConnectAsync().GetAwaiter().GetResult()

  let settings: CatchUpSubscriptionSettings =
    CatchUpSubscriptionSettings
      (maxLiveQueueSize = 100,
       readBatchSize = 10,
       verboseLogging = false,
       resolveLinkTos = true,
       subscriptionName = name)

  let subscribe (state: SubscriptionState) (mailbox: SubscriptionMailbox) =
    let eventAppeared =
      Func<EventStoreCatchUpSubscription, ResolvedEvent, Task>(fun _ resolvedEvent ->
        let t = unitTask {
          state.CancellationToken.ThrowIfCancellationRequested()
          let checkpoint = Checkpoint.StreamPosition resolvedEvent.Event.EventNumber
          match SerializedRecordedEvent.FromEventStoreResolvedEvent(resolvedEvent) with
          | Error e ->
            log.LogError($"Error while deserializing event %A{e}")
          | Ok encodedEvent ->
            do! eventHandler encodedEvent
            mailbox.Post(EventAppeared checkpoint)
        }
        t)

    let subscriptionDropped =
      Action<EventStoreCatchUpSubscription, SubscriptionDropReason, exn>(fun _ reason err ->
        mailbox.Post(Dropped(reason, err))
      )

    log.LogDebug($"Subscribing to {streamId} from checkpoint {state.Checkpoint}")
    let lastCheckpoint =
      match state.Checkpoint with
      | Checkpoint.StreamStart -> StreamCheckpoint.StreamStart
      | Checkpoint.StreamPosition pos -> Nullable(pos)
    let subscription =
      conn.SubscribeToStreamFrom(
        settings = settings,
        stream = streamId.Value,
        lastCheckpoint = lastCheckpoint,
        eventAppeared = eventAppeared,
        subscriptionDropped = subscriptionDropped
        )
    mailbox.Post(Subscribed subscription)

  let evolve (mailbox: SubscriptionMailbox): SubscriptionState -> SubscriptionMessage -> SubscriptionState =
    fun state -> function
      | Subscribe ->
        match state.SubscriptionStatus with
        | SubscriptionStatus.UnSubscribed ->
          subscribe state mailbox
          state
        | _ -> state
      | Subscribed _ ->
        { state with SubscriptionStatus = SubscriptionStatus.Subscribed }
      | Dropped (reason, err) ->
        do
          match reason with
          | SubscriptionDropReason.ServerError
          | SubscriptionDropReason.EventHandlerException
          | SubscriptionDropReason.ProcessingQueueOverflow
          | SubscriptionDropReason.CatchUpError
          | SubscriptionDropReason.ConnectionClosed ->
            log.LogDebug($"error: %A{err}\nSubscription dropped: {reason}; reconnecting...")
            Thread.Sleep(1000)
            mailbox.Post(Subscribe)
          | _ ->
            log.LogError($"error: %A{err}\nSubscription dropped: {reason};")
        { state with SubscriptionStatus = SubscriptionStatus.UnSubscribed }
      | EventAppeared checkpoint ->
        { state with Checkpoint = checkpoint }
      | GetState channel ->
        channel.Reply(state)
        state
  let start (initialState: SubscriptionState) =
    let agentBody =
      fun (inbox: SubscriptionMailbox) ->
        let rec loop (state: SubscriptionState) = async {
          let! item =  inbox.Receive()
          return! loop (evolve inbox state item)
        }
        loop initialState
    SubscriptionMailbox.Start(agentBody, initialState.CancellationToken)
  let rec watch (mailbox: SubscriptionMailbox) = async {
    let! state = mailbox.PostAndAsyncReply(GetState)
    log.LogDebug($"Stream {streamId} is at checkpoint {state.Checkpoint}")
    do! Async.Sleep 10000
    return! watch mailbox
  }


  member this.SubscribeAsync(checkpoint: Checkpoint, ct: CancellationToken) = async {
    let initState = {
      Checkpoint = checkpoint
      CancellationToken = ct
      SubscriptionStatus = SubscriptionStatus.UnSubscribed }
    let mailbox = start initState
    do! watch mailbox
  }
