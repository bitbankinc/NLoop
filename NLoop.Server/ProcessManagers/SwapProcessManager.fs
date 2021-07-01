namespace NLoop.Server.ProcessManagers

open System.Collections.Generic
open FSharp.Control.Tasks
open FSharp.Control.Reactive
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Domain
open NLoop.Server.Actors

type SwapProcessManager(eventAggregator: IEventAggregator,
                        lightningClientProvider: ILightningClientProvider,
                        actor: SwapActor,
                        listeners: IEnumerable<ISwapEventListener>) =
  let obs = eventAggregator.GetObservable<RecordedEvent<Swap.Event>>()
  let _ =
    obs
    |> Observable.choose(fun e ->
      match e.Data with
      | Swap.Event.OffChainOfferStarted(swapId, pairId, invoice) -> Some(swapId, pairId, invoice)
      | _ -> None)
    |> Observable.flatmapTask(fun (swapId, struct(ourCC, _theirCC), invoice) ->
      task {
        let! p = lightningClientProvider.GetClient(ourCC).Offer(invoice)
        do! actor.Execute(swapId, Swap.Command.OffChainOfferResolve(p), nameof(SwapProcessManager))
      })
    |> Observable.subscribe(id)

  let _ =
    obs
    |> Observable.iter(fun e ->
      match e.Data with
      | Swap.Event.SuccessfullyFinished swapId
      | Swap.Event.FinishedByRefund swapId
      | Swap.Event.LoopErrored (swapId, _) ->
        for l in listeners do
          l.RemoveSwap swapId
      | _ -> ()
    )
    |> Observable.subscribe(ignore)
