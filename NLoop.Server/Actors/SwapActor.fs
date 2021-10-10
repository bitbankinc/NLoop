namespace NLoop.Server.Actors

open System
open System.Threading.Tasks
open DotNetLightning.Payment
open FSharp.Control.Tasks
open FSharp.Control.Reactive
open LndClient
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.RPC
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server

[<AutoOpen>]
module private Helpers =
  let getSwapDeps b f u g payInvoice =
    { Swap.Deps.Broadcaster = b
      Swap.Deps.FeeEstimator = f
      Swap.Deps.UTXOProvider = u
      Swap.Deps.GetChangeAddress = g
      Swap.Deps.GetRefundAddress = g
      Swap.Deps.PayInvoice = payInvoice
      }

type SwapActor(broadcaster: IBroadcaster,
               feeEstimator: IFeeEstimator,
               utxoProvider: IUTXOProvider,
               getChangeAddress: GetAddress,
               lightningClient: ILightningClientProvider,
               opts: IOptions<NLoopOptions>,
               logger: ILogger<SwapActor>,
               eventAggregator: IEventAggregator
  )  =

  let aggr =
    let payInvoice =
      fun (n: Network) (param: Swap.PayInvoiceParams) (i: PaymentRequest) ->
        let cc = n.ChainName.ToString() |> SupportedCryptoCode.TryParse
        let req = {
          SendPaymentRequest.Invoice = i
          MaxFee = param.MaxFee |> FeeLimit.Fixed
          OutgoingChannelId = param.OutgoingChannelId
        }
        lightningClient.GetClient(cc.Value).Offer(req) :> Task
    getSwapDeps broadcaster feeEstimator utxoProvider getChangeAddress payInvoice
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
        eventAggregator.Publish({ Swap.EventWithId.Id = swapId; Swap.EventWithId.Event = e.Data })
      )
    | Error (EventSourcingError.Store s as e) ->
      logger.LogError($"Store Error when executing swap handler %A{s}")
      eventAggregator.Publish({ Swap.ErrorWithId.Id = swapId; Swap.ErrorWithId.Error = e })
      // todo: retry
      ()
    | Error s ->
      logger.LogError($"Error when executing swap handler %A{s}")
      eventAggregator.Publish({ Swap.ErrorWithId.Id = swapId; Swap.ErrorWithId.Error = s })
  }
