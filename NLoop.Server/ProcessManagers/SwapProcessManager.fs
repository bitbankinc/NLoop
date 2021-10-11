namespace NLoop.Server.ProcessManagers

open System.Collections.Generic
open System.Threading.Tasks
open FSharp.Control.Tasks
open FSharp.Control.Reactive
open LndClient
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Domain
open NLoop.Server.Actors

type SwapProcessManager(eventAggregator: IEventAggregator,
                        lightningClientProvider: ILightningClientProvider,
                        actor: SwapActor,
                        logger: ILogger<SwapProcessManager>,
                        listeners: IEnumerable<ISwapEventListener>) =
  let obs = eventAggregator.GetObservable<RecordedEvent<Swap.Event>>()

  let mutable subsc1 = null
  let mutable subsc2 = null

  let handleError swapId msg  = unitTask {
    logger.LogError($"{msg}")
    do! actor.Execute(swapId, Swap.Command.SetValidationError msg, nameof(SwapProcessManager))
  }
  interface IHostedService with
    member this.StartAsync(_ct) =
      subsc1 <-
        obs
        |> Observable.choose(fun e ->
          match e.Data with
          | Swap.Event.OffChainOfferStarted(swapId, pairId, invoice, paymentParams) -> Some(swapId, pairId, invoice, paymentParams)
          | _ -> None)
        |> Observable.flatmapTask(fun (swapId, struct(_baseAsset, quoteAsset), invoice, paymentParams) ->
          task {
            try
              let! pr =
                let req = {
                  SendPaymentRequest.Invoice = invoice
                  MaxFee = paymentParams.MaxFee |> FeeLimit.Fixed
                  OutgoingChannelId = paymentParams.OutgoingChannelId
                }
                lightningClientProvider.GetClient(quoteAsset).Offer(req).ConfigureAwait(false)

              match pr with
              | Ok _ ->
                ()
              | Error e ->
                do! handleError swapId e
            with
            | ex ->
                do! handleError swapId ex.Message
          })
        |> Observable.subscribe(id)

      subsc2 <-
        obs
        |> Observable.subscribe(fun e ->
          match e.Data with
          | Swap.Event.NewLoopOutAdded(_, { Id = swapId })
          | Swap.Event.NewLoopInAdded(_, { Id = swapId }) ->
            // TODO: re-registering everything from start is not very performant nor scalable.
            // Ideally we should register only the one which is not finished.
            logger.LogDebug($"Registering new Swap {swapId}")
            for l in listeners do
              l.RegisterSwap(swapId)
          | Swap.Event.FinishedSuccessfully swapId
          | Swap.Event.FinishedByRefund swapId
          | Swap.Event.FinishedByError (swapId, _) ->
            logger.LogDebug($"Removing Finished Swap {swapId}")
            for l in listeners do
              l.RemoveSwap swapId
          | _ -> ()
        )
      Task.CompletedTask

    member this.StopAsync(_ct) =
      do
        subsc1.Dispose()
        subsc2.Dispose()
      Task.CompletedTask
