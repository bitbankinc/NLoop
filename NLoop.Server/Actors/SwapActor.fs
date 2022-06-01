namespace NLoop.Server.Actors

open System
open System.Threading.Channels
open FSharp.Control
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils
open FSharp.Control.Tasks
open FSharp.Control.Reactive
open FsToolkit.ErrorHandling
open LndClient
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Server.DTOs
open System.Reactive.Linq

[<AutoOpen>]
module internal SwapActorHelpers =
  let getSwapDeps b f g payInvoice payToAddress offer =
    { Swap.Deps.Broadcaster = b
      Swap.Deps.FeeEstimator = f
      Swap.Deps.GetRefundAddress = g
      Swap.Deps.PayInvoiceImmediate = payInvoice
      Swap.Deps.PayToAddress = payToAddress
      Swap.Deps.Offer = offer
      }

  let getObs (eventAggregator: IEventAggregator) (sId) =
      eventAggregator.GetObservable<Swap.EventWithId, Swap.ErrorWithId>()
      |> Observable.filter(function
                           | Choice1Of2 { Id = swapId }
                           | Choice2Of2 { Id = swapId } -> swapId = sId)


[<RequireQualifiedAccess>]
module Observable =
  let inline chooseOrError
    (selector: Swap.Event -> _ option)
    (obs: IObservable<Choice<Swap.EventWithId, Swap.ErrorWithId>>) =
      obs
      |> Observable.choose(
        function
        | Choice1Of2{ Event = Swap.Event.FinishedByError { Error = err } } -> err |> Error |> Some
        | Choice2Of2{ Error = DomainError e } -> e.Msg |> Error |> Some
        | Choice2Of2{ Error = Store(StoreError e) } -> e |> Error |> Some
        | Choice1Of2{ Event = e } -> selector e |> Option.map(Ok)
      )
      |> Observable.catchWith(fun ex -> Observable.Return(Error $"Error while handling observable {ex}"))
      |> Observable.first
      |> fun t -> t.GetAwaiter() |> Async.AwaitCSharpAwaitable

type SwapActor(opts: GetOptions,
               lightningClientProvider: ILightningClientProvider,
               broadcaster: IBroadcaster,
               feeEstimator: IFeeEstimator,
               eventAggregator: IEventAggregator,
               getAllSwapEvents: GetAllEvents<Swap.Event>,
               getRefundAddress: GetAddress,
               getWalletClient: GetWalletClient,
               store: Store,
               logger: ILogger<SwapActor>) =
  let aggr =
    let payInvoiceImmediate =
      fun (cc: SupportedCryptoCode) (param: Swap.PayInvoiceParams) (i: PaymentRequest) ->
        let req = {
          SendPaymentRequest.Invoice = i
          MaxFee = param.MaxFee
          OutgoingChannelIds = param.OutgoingChannelIds
          TimeoutSeconds = Constants.OfferTimeoutSeconds
        }
        task {
          match! lightningClientProvider.GetClient(cc).SendPayment(req) with
          | Ok r ->
            return {
              Swap.PayInvoiceResult.RoutingFee = r.Fee
              Swap.PayInvoiceResult.AmountPayed = i.AmountValue.Value
            }
          | Error e -> return raise <| exn $"Failed payment {e}"
        }
    let fundFromWallet =
      fun (req: WalletFundingRequest) -> task {
        let cli = getWalletClient(req.CryptoCode)
        let! txid = cli.FundToAddress(req.DestAddress, req.Amount, req.TargetConf)
        let blockchainCli = opts().GetBlockChainClient(req.CryptoCode)
        return! blockchainCli.GetRawTransaction(TxId txid)
      }
    let offer =
      fun (cc: SupportedCryptoCode) (param: Swap.PayInvoiceParams) (i: PaymentRequest) ->
        let req = {
          SendPaymentRequest.Invoice = i
          MaxFee = param.MaxFee
          OutgoingChannelIds = param.OutgoingChannelIds
          TimeoutSeconds = Constants.OfferTimeoutSeconds
        }
        lightningClientProvider.GetClient(cc).Offer(req)
    getSwapDeps broadcaster feeEstimator getRefundAddress payInvoiceImmediate fundFromWallet offer
    |> Swap.getAggregate
  let mutable handler =
    Swap.getHandler aggr store

  /// We use queue to assure the change to the command execution is sequential.
  /// This is OK (since performance rarely be a consideration in swap) but it is
  /// not ideal in terms of performance, ideally we should allow a concurrent update
  /// and handle the StoreError (e.g. retry or abort)
  /// :todo:
  let workQueue = Channel.CreateBounded<SwapId * ESCommand<Swap.Command> * bool> 10

  let _worker = task {
    let mutable finished = false
    while not <| finished do
      let! channelOpened = workQueue.Reader.WaitToReadAsync()
      finished <- not <| channelOpened
      if not finished then
        let! swapId, cmd, commitError = workQueue.Reader.ReadAsync()
        match! handler.Execute swapId cmd with
        | Ok events ->
          logger.LogDebug $"executed command: {cmd.Data.CommandTag} successfully"
          events
          |> List.iter(fun e ->
            eventAggregator.Publish e
            eventAggregator.Publish e.Data
            eventAggregator.Publish({ Swap.EventWithId.Id = swapId; Swap.EventWithId.Event = e.Data })
          )
        | Error (EventSourcingError.Store s as e) ->
          logger.LogError($"Store Error when executing the swap handler %A{s}")
          eventAggregator.Publish({ Swap.ErrorWithId.Id = swapId; Swap.ErrorWithId.Error = e })
          // todo: retry
          ()
        | Error (EventSourcingError.DomainError e as s) ->
          logger.LogError($"Error when executing swap handler %A{e}")
          eventAggregator.Publish({ Swap.ErrorWithId.Id = swapId; Swap.ErrorWithId.Error = s })
          if commitError then
            let cmd =
              { ESCommand.Data = Swap.Command.MarkAsErrored (e.Msg)
                Meta = { CommandMeta.Source = $"{nameof(SwapActor)}-errorhandler"
                         EffectiveDate = UnixDateTime.UtcNow } }
            try
              do! workQueue.Writer.WriteAsync((swapId, cmd, false))
            with
            | _ex ->
              ()
  }

  member val Handler = handler with get
  member val Aggregate = aggr with get
  member this.Execute(swapId, msg: Swap.Command, source, commitErrorOnFailure: bool option) = unitTask {
    let commitErrorOnFailure = defaultArg commitErrorOnFailure false
    let source = defaultArg source (nameof(SwapActor))
    logger.LogDebug($"New Command {msg} for swap {swapId}: (source {source})")
    let cmd =
      { ESCommand.Data = msg
        Meta = { CommandMeta.Source = source
                 EffectiveDate = UnixDateTime.UtcNow } }

    let! channelOpened = workQueue.Writer.WaitToWriteAsync()
    if channelOpened then
      do! workQueue.Writer.WriteAsync((swapId, cmd, commitErrorOnFailure))
  }

  interface ISwapActor with
    member this.Aggregate = this.Aggregate
    member this.Execute(i, cmd, s, commitError) =
      this.Execute(i, cmd, s, commitError)

    member this.GetAllEntities(since, ?ct: CancellationToken) = task {
      let ct = defaultArg ct CancellationToken.None
      let! events = getAllSwapEvents since ct
      let eventListToStateMap (l: RecordedEvent<Swap.Event> list) =
        l
        |> List.groupBy(fun re -> re.StreamId)
        |> List.filter(fun (_streamId, reList) ->
          reList |> List.isEmpty |> not &&
            (reList.Head.Data.Type = Swap.new_loop_out_added || reList.Head.Data.Type = Swap.new_loop_in_added)
        )
        |> List.map(fun (streamId, reList) ->
          streamId,
          reList |> List.map(fun re -> re.AsEvent) |> this.Handler.Reconstitute
        )
        |> Map.ofList
      return
        events
        |> Result.map eventListToStateMap
    }
    member this.Handler = this.Handler

type ISwapExecutor =
  abstract member ExecNewLoopOut:
    req: LoopOutRequest *
    height: BlockHeight *
    ?s: string *
    ?ct: CancellationToken -> Task<Result<LoopOutResponse, string>>
  abstract member ExecNewLoopIn:
    req: LoopInRequest *
    height: BlockHeight *
    ?s: string *
    ?ct: CancellationToken -> Task<Result<LoopInResponse, string>>
