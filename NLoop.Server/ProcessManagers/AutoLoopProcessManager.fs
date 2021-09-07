namespace NLoop.Server.ProcessManagers

open System.Collections.Generic
open System.Threading.Tasks
open DotNetLightning.Utils
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
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
                            logger: ILogger<SwapProcessManager>) =
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
        t
      | _ -> None
      )
    |> Observable.scan(fun acc re ->
      acc |> Map.add re ()
    ) Map.empty

  interface IHostedService with
    member this.StartAsync(ct) =
      subsc1 <-
        obs
        |> Observable.subscribe(fun re ->
          match re.Data with
          | AutoLoop.Event.NewRuleAdded rule ->
            this.ActiveRules.Add(rule.Channel, rule)
            ()
          | _ -> ()
        )
      Task.CompletedTask

    member this.StopAsync(cancellationToken) =
      subsc1.Dispose()
      Task.CompletedTask
