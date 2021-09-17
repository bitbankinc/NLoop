namespace NLoop.Server.ProcessManagers

open System
open System.Collections.Generic
open System.CommandLine
open System.Threading.Tasks
open FSharp.Control.Tasks
open DotNetLightning.Utils
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.Projections

type AutoLoopProcessManager(eventAggregator: IEventAggregator,
                            lightningClientProvider: ILightningClientProvider,
                            actor: AutoLoopActor,
                            swapState: SwapStateProjection,
                            opts: IOptions<NLoopOptions>,
                            logger: ILogger<SwapProcessManager>) =
  inherit BackgroundService()
  let obs = eventAggregator.GetObservable<RecordedEvent<AutoLoop.Event>>()
  let mutable subsc1 = null

  member val ActiveRules = Map.empty<ShortChannelId, AutoLoopRule> with get

  member this.ActiveChannels =
    eventAggregator.GetObservable<RecordedEvent<Swap.Event>>()
    |> Observable.choose(fun re ->
      match re.Data with
      | Swap.Event.NewLoopInAdded (height, loopOut) ->
        loopOut.Id |> Choice1Of2 |> Some
      | Swap.Event.NewLoopInAdded (height, loopIn) ->
        loopIn.Id |> Choice1Of2 |> Some
      | Swap.Event.FinishedSuccessfully t ->
        None
      | _ -> None
      )
    |> Observable.scan(fun acc re ->
      acc |> Map.add re ()
    ) Map.empty

  override this.ExecuteAsync(ct) = unitTask {
    let clis: (_ * _ * _) seq =
      opts.Value.OnChainCrypto
      |> Seq.distinct
      |> Seq.map(fun x ->
        (opts.Value.GetRPCClient x, lightningClientProvider.GetClient(x), x))
    try
      while not <| ct.IsCancellationRequested do
        for rpcClient, lightningClient, cc in clis do
          let! chs = lightningClient.ListChannels()
          return failwith "todo"
    with
    | :? OperationCanceledException ->
      logger.LogInformation($"Stopping {nameof(AutoLoopProcessManager)}...")
    | ex ->
      logger.LogError($"{ex}")
  }
