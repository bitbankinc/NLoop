namespace NLoop.Server.ProcessManagers

open System
open System.Collections.Generic
open System.Reactive.Subjects
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils
open FSharp.Control.Tasks
open FSharp.Control.Reactive
open LndClient
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Domain
open NLoop.Server.Actors
open FsToolkit.ErrorHandling
open NLoop.Server.Projections
open FSharp.Control

type SwapProcessManager(eventAggregator: IEventAggregator,
                        lightningClientProvider: ILightningClientProvider,
                        actor: ISwapActor,
                        logger: ILogger<SwapProcessManager>,
                        swapState: IOnGoingSwapStateProjection,
                        listeners: IEnumerable<ISwapEventListener>) =
  let obs =
    eventAggregator.GetObservable<RecordedEvent<Swap.Event>>()

  let subscriptions = ResizeArray()
  let handleError swapId msg  = unitTask {
    logger.LogError($"{msg}")
    do! actor.Execute(swapId, Swap.Command.MarkAsErrored msg, nameof(SwapProcessManager))
  }
  let rec waitToOfferGetsResolved (swapId, quote, invoice: PaymentRequest) =
    asyncSeq {
      try
        let seq = lightningClientProvider.GetClient(quote).TrackPayment(invoice.PaymentHash)
        for s in seq do
          match s.InvoiceState with
          | OutgoingInvoiceStateUnion.Succeeded ->
            let cmd =
              {
                Swap.PayInvoiceResult.RoutingFee = s.Fee
                Swap.PayInvoiceResult.AmountPayed = s.AmountPayed
              }
              |> Swap.Command.OffChainOfferResolve
            do!
              actor.Execute(swapId, cmd, $"{nameof(SwapProcessManager)}-{nameof(waitToOfferGetsResolved)}", true)
              |> Async.AwaitTask
          | OutgoingInvoiceStateUnion.Failed ->
            let msg = "Offchain payment failed"
            do! handleError swapId msg |> Async.AwaitTask
          | OutgoingInvoiceStateUnion.InFlight ->
            ()
          | OutgoingInvoiceStateUnion.Unknown ->
            logger.LogWarning $"Unknown invoice state detected."
      with
      | ex ->
        let msg = $"error on {nameof(waitToOfferGetsResolved)}. {ex.ToString()}"
        do! handleError swapId msg |> Async.AwaitTask
      return ()
    }
    |> AsyncSeq.toArrayAsync
    |> Async.Ignore
    |> Async.StartAsTask

  let startup() =
    logger.LogInformation $"starting {nameof(SwapProcessManager)}..."
    let subsc1 =
      obs
      |> Observable.choose(fun e ->
        match e.Data with
        | Swap.Event.OffChainOfferStarted d ->
          // we want to execute the side effect again only when we haven't finished before.
          let isOffchainOfferResolved =
            let maybeState = swapState.State |> Map.tryFind e.StreamId
            match maybeState with
            | Some(_, Swap.State.Out(_, o)) -> o.IsOffchainOfferResolved
            | _ -> false
          if isOffchainOfferResolved then
            None
          else
            Some (d.SwapId, d.PairId.Quote, d.Invoice |> ResultUtils.Result.deref)
        | _ -> None)
      |> Observable.flatmapTask(waitToOfferGetsResolved)
      |> Observable.subscribe id
    subscriptions.Add subsc1
    let subsc2 =
      obs
      |> Observable.subscribe(fun e ->
        match e.Data with
        | Swap.Event.NewLoopOutAdded { LoopOut = { Id = swapId; PairId = pairId } } ->
          logger.LogDebug($"Registering new Swap {swapId}")
          let group = {
            Swap.Group.Category = Swap.Category.Out
            Swap.Group.PairId = pairId
          }
          try
            for l in listeners do
              l.RegisterSwap(swapId, group)
          with
          | ex ->
            logger.LogError $"Failed to register swap (id: {swapId}, group: {group}) {ex}"
        | Swap.Event.NewLoopInAdded { LoopIn = { Id = swapId; PairId = pairId} } ->
          logger.LogDebug($"Registering new Swap {swapId}")
          let group = {
            Swap.Group.Category = Swap.Category.In
            Swap.Group.PairId = pairId
          }
          try
            for l in listeners do
              l.RegisterSwap(swapId, group)
          with
          | ex ->
            logger.LogError $"Failed to register swap (id: {swapId}) {ex}"
        | Swap.Event.FinishedSuccessfully { Id = swapId }
        | Swap.Event.FinishedByRefund { Id = swapId }
        | Swap.Event.FinishedByTimeout { Id = swapId }
        | Swap.Event.FinishedByError { Id = swapId } ->
          logger.LogDebug($"Removing Finished Swap {swapId}")
          try
            for l in listeners do
              l.RemoveSwap swapId
          with
          | ex ->
            logger.LogError $"Failed to remove swap (id: {swapId}) {ex}"
        | _ -> ()
      )
    subscriptions.Add subsc2
  do startup()

  interface IDisposable with
    member this.Dispose() =
      for s in subscriptions do
        s.Dispose()

  // We want this interface just to assure this will start before the SwapProjection emits an events.
  interface IHostedService with
    member this.StartAsync(_ct) =
      Task.CompletedTask
    member this.StopAsync(_ct) =
      Task.CompletedTask
