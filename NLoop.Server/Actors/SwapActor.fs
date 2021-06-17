namespace NLoop.Server.Actors

open System
open FSharp.Control.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils.EventStore
open NLoop.Domain.Utils
open NLoop.Server

[<AutoOpen>]
module private Helpers =
  let getSwapDeps b f u g l =
    { Swap.Deps.Broadcaster = b
      Swap.Deps.FeeEstimator = f
      Swap.Deps.UTXOProvider = u
      Swap.Deps.GetChangeAddress = g
      Swap.Deps.LightningClient = l }

  let handleEvent (eventAggregator: IEventAggregator) : EventHandler =
    fun (event) -> unitTask {
      event.ToRecordedEvent(Swap.serializer)
      |> Result.map(eventAggregator.Publish<RecordedEvent<Swap.Event>>)
      |> ignore
    }
type SwapActor(broadcaster: IBroadcaster,
               feeEstimator: IFeeEstimator,
               utxoProvider: IUTXOProvider,
               getChangeAddress: GetChangeAddress,
               lightningClientProvider: ILightningClientProvider,
               opts: IOptions<NLoopOptions>,
               logger: ILogger<SwapActor>,
               eventAggregator: IEventAggregator
  ) =

  let handler =
    let aggr =
      getSwapDeps broadcaster feeEstimator utxoProvider getChangeAddress (lightningClientProvider.AsDomainLNClient())
      |> Swap.getAggregate
    Swap.getHandler aggr (opts.Value.EventStoreUrl |> Uri)

  member this.Execute(msg: Swap.Msg, ?source) = task {
    let source = source |> Option.defaultValue "SwapActor"
    let cmd =
      { Command.Data = msg
        Meta = { CommandMeta.Source = source
                 EffectiveDate = DateTime.Now } }
    match! handler.Execute msg.SwapId cmd with
    | Ok events ->
      events
      |> List.iter eventAggregator.Publish
    | Error s ->
      logger.LogError($"Error when executing swap handler %A{s}")
      eventAggregator.Publish(s)
  }


type SwapListener(loggerFactory: ILoggerFactory,
                  opts: IOptions<NLoopOptions>,
                  eventAggregator: IEventAggregator) =

  let subscription =
    EventStoreDBSubscription(
      { EventStoreConfig.Uri = opts.Value.EventStoreUrl |> Uri },
      "swap actor",
      Swap.streamId,
      loggerFactory.CreateLogger(),
      handleEvent eventAggregator)

  interface IHostedService with
    member this.StartAsync(stoppingToken) = unitTask {
      return failwith "todo"
    }

    member this.StopAsync(cancellationToken) = unitTask {
      return failwith "todo"
    }
