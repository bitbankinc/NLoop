namespace NLoop.Server.ProcessManagers

open System.Collections.Generic
open System.Threading.Tasks
open System.Reactive.Linq
open FSharp.Control.Tasks
open FSharp.Control.Reactive
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

  interface IHostedService with
    member this.StartAsync(ct) =
      subsc1 <-
        obs
        |> Observable.choose(fun e ->
          match e.Data with
          | Swap.Event.OffChainOfferStarted(swapId, pairId, invoice) -> Some(swapId, pairId, invoice)
          | _ -> None)
        |> Observable.flatmapTask(fun (swapId, struct(ourCC, _theirCC), invoice) ->
          task {
            try
              printfn "DEBUG: staring offer..."
              let! p = lightningClientProvider.GetClient(ourCC).Offer(invoice, ct).ConfigureAwait(false)
              printfn "DEBUG: finished offer..."
              do! actor.Execute(swapId, Swap.Command.OffChainOfferResolve(p), nameof(SwapProcessManager))
            with
            | ex ->
              logger.LogError($"{ex}")
              do! actor.Execute(swapId, Swap.Command.SetValidationError(ex.Message))
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
          | Swap.Event.SuccessfullyFinished swapId
          | Swap.Event.FinishedByRefund swapId
          | Swap.Event.LoopErrored (swapId, _) ->
            logger.LogDebug($"Removing new Swap {swapId}")
            for l in listeners do
              l.RemoveSwap swapId
          | _ -> ()
        )
      Task.CompletedTask

    member this.StopAsync(ct) =
      do
        subsc1.Dispose()
        subsc2.Dispose()
      Task.CompletedTask
