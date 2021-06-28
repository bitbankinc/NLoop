namespace NLoop.Server.ProcessManagers

open FSharp.Control.Tasks
open FSharp.Control.Reactive
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Domain
open NLoop.Server.Actors

type SwapProcessManager(eventAggregator: IEventAggregator,
                        lightningClientProvider: ILightningClientProvider,
                        actor: SwapActor) =
  let obs = eventAggregator.GetObservable<RecordedEvent<Swap.Event>>()
  let _ =
    obs
    |> Observable.choose(fun e ->
      match e.Data with
      | Swap.Event.OffChainOfferStarted(swapId, pairId, invoice) -> Some(swapId, pairId, invoice)
      | _ -> None)
    |> Observable.flatmapAsync(fun (swapId, struct(ourCC, _theirCC), invoice) ->
      async {
        let! p = lightningClientProvider.GetClient(ourCC).Offer(invoice) |> Async.AwaitTask
        do! actor.Execute(swapId, Swap.Command.OffChainPaymentReception(p), nameof(SwapProcessManager)) |> Async.AwaitTask
      })
    |> Observable.subscribe(id)


  let _ =
    obs
    |> Observable.choose(fun e ->
      match e.Data with
      | Swap.Event.SwapTxTimeout txid -> Some txid
      | _ -> None
      )
    |> fun x -> failwith "todo: claim tx"
