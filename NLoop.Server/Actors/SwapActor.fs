namespace NLoop.Server.Actors

open System
open FSharp.Control.Tasks
open FSharp.Control.Reactive
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server

[<AutoOpen>]
module private Helpers =
  let getSwapDeps b f u g =
    { Swap.Deps.Broadcaster = b
      Swap.Deps.FeeEstimator = f
      Swap.Deps.UTXOProvider = u
      Swap.Deps.GetChangeAddress = g
      Swap.Deps.GetRefundAddress = g }

type SwapEventWithId = {
  Id: SwapId
  Data: Swap.Event
}

type SwapErrorWithId = {
  Id: SwapId
  Error: EventSourcingError<Swap.Error>
}

type SwapActor(broadcaster: IBroadcaster,
               feeEstimator: IFeeEstimator,
               utxoProvider: IUTXOProvider,
               getChangeAddress: GetAddress,
               opts: IOptions<NLoopOptions>,
               logger: ILogger<SwapActor>,
               eventAggregator: IEventAggregator
  )  =

  let aggr =
    getSwapDeps broadcaster feeEstimator utxoProvider getChangeAddress
    |> Swap.getAggregate
  let handler =
    Swap.getHandler aggr (opts.Value.EventStoreUrl |> Uri)

  member val Handler = handler with get
  member val Aggregate = aggr with get

  member this.Execute(swapId, msg: Swap.Command, ?source) = task {
    logger.LogDebug($"New Command {msg}")
    let source = source |> Option.defaultValue (nameof(SwapActor))
    let cmd =
      { ESCommand.Data = msg
        Meta = { CommandMeta.Source = source
                 EffectiveDate = UnixDateTime.UtcNow } }
    match! handler.Execute swapId cmd with
    | Ok events ->
      events
      |> List.iter(fun e ->
        eventAggregator.Publish e
        eventAggregator.Publish e.Data
        eventAggregator.Publish({ Id = swapId; Data = e.Data })
      )
    | Error s ->
      logger.LogError($"Error when executing swap handler %A{s}")
      eventAggregator.Publish({ Id = swapId; Error = s })
  }
