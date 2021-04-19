namespace NLoop.Server.Actors

open System.Threading.Tasks
open Microsoft.Extensions.Logging
open NLoop.Domain
open NLoop.Server

type SwapActor(logger: ILoggerFactory, broadcaster: IBroadcaster, feeEstimator: IFeeEstimator, eventAggregator: EventAggregator) =
  inherit Actor<Swap.State, Swap.Command, Swap.Event, Swap.Error>({ Zero = Swap.State.Create(logger.CreateLogger<Swap.Aggregate>(), broadcaster, feeEstimator)
                                                                    Apply = Swap.applyChanges
                                                                    Exec = Swap.executeCommand }, logger.CreateLogger<SwapActor>())

  let logger = logger.CreateLogger<SwapActor>()
  override this.HandleError(var0) =
    logger.LogError($"{var0}")
    Task.CompletedTask
  override this.PublishEvent(e) =
    eventAggregator.Publish(e) |> Task.FromResult :> Task

