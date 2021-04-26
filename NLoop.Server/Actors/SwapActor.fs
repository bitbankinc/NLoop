namespace NLoop.Server.Actors

open System.Threading.Tasks
open Microsoft.Extensions.Logging
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server

type SwapActor(logger: ILoggerFactory, broadcaster: IBroadcaster, feeEstimator: IFeeEstimator, eventAggregator: EventAggregator) =
  inherit Actor<Swap.State, Swap.Command, Swap.Event, Swap.Error, Swap.Deps>
    ({ Zero = Swap.State.Zero
       Apply = Swap.applyChanges
       Exec = Swap.executeCommand },
       { Swap.Deps.Broadcaster = broadcaster; Swap.Deps.FeeEstimator = feeEstimator },
       logger.CreateLogger<SwapActor>())

  let logger = logger.CreateLogger<SwapActor>()
  override this.HandleError(error) =
    logger.LogError($"{error}")
    Task.CompletedTask
  override this.PublishEvent(evt) =
    eventAggregator.Publish(evt)
    Task.CompletedTask

