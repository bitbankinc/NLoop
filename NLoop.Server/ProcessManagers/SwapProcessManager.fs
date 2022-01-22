namespace NLoop.Server.ProcessManagers

open System.Collections.Generic
open System.Threading.Tasks
open DotNetLightning.Payment
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

type SwapProcessManager(eventAggregator: IEventAggregator,
                        lightningClientProvider: ILightningClientProvider,
                        actor: ISwapActor,
                        logger: ILogger<SwapProcessManager>,
                        listeners: IEnumerable<ISwapEventListener>) =
  let obs = eventAggregator.GetObservable<RecordedEvent<Swap.Event>>()

  let subscriptions = ResizeArray()
  let handleError swapId msg  = unitTask {
    logger.LogError($"{msg}")
    do! actor.Execute(swapId, Swap.Command.MarkAsErrored msg, nameof(SwapProcessManager))
  }
  interface IHostedService with
    member this.StartAsync(_ct) =
      logger.LogInformation $"starting {nameof(SwapProcessManager)}..."
      let subsc1 =
        obs
        |> Observable.choose(fun e ->
          match e.Data with
          | Swap.Event.OffChainOfferStarted d ->
            Some d
          | _ -> None)
        |> Observable.flatmapTask(fun ({ SwapId = swapId; PairId = pairId; Params = paymentParams } as data)->
          task {
            try
              let invoice = data.Invoice |> ResultUtils.Result.deref
              let! pr =
                let req = {
                  SendPaymentRequest.Invoice = invoice
                  MaxFee = paymentParams.MaxFee
                  OutgoingChannelIds = paymentParams.OutgoingChannelIds
                }
                lightningClientProvider.GetClient(pairId.Quote).Offer(req).ConfigureAwait(false)

              match pr with
              | Ok s ->
                let  cmd =
                  { Swap.PayInvoiceResult.RoutingFee = s.Fee; Swap.PayInvoiceResult.AmountPayed = invoice.AmountValue.Value }
                  |> Swap.Command.OffChainOfferResolve
                do! actor.Execute(swapId, cmd, nameof(SwapProcessManager))
              | Error e ->
                do! handleError swapId e
            with
            | ex ->
                do! handleError swapId (ex.ToString())
          })
        |> Observable.subscribe(id)
      subscriptions.Add subsc1
      let subsc2 =
        obs
        |> Observable.subscribe(fun e ->
          match e.Data with
          | Swap.Event.NewLoopOutAdded { LoopOut = { Id = swapId; PairId = pairId } } ->
            // TODO: re-registering everything from start is not very performant nor scalable.
            // Ideally we should register only the one which is not finished.
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
            // TODO: re-registering everything from start is not very performant nor scalable.
            // Ideally we should register only the one which is not finished.
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
      Task.CompletedTask


    member this.StopAsync(_ct) =
      for s in subscriptions do
        s.Dispose()
      Task.CompletedTask
