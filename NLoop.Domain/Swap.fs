namespace NLoop.Domain

open System
open System
open System.Linq
open System.Text.Json
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Domain
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open FSharp.Control.Tasks
open FsToolkit.ErrorHandling
open NLoop.Domain.Utils.EventStore

[<RequireQualifiedAccess>]
module Swap =
  // ------ state -----
  type SwapList = {
    Out: LoopOut list
    In: LoopIn list
  }

  type State = {
    OnGoing: SwapList
  }
    with
    static member Zero = {
      State.OnGoing = { Out = []; In = [] }
    }

  // ------ command -----

  module Data =
    type TxInfo = {
      TxId: uint256
      Tx: Transaction
      Eta: int
    }
    and SwapStatusResponseData = {
      _Status: string
      Transaction: TxInfo option
      FailureReason: string option
    }
      with
      member this.SwapStatus =
        match this._Status with
        | "swap.created" -> SwapStatusType.SwapCreated
        | "swap.expired" -> SwapStatusType.SwapExpired

        | "invoice.set" -> SwapStatusType.InvoiceSet
        | "invoice.payed" -> SwapStatusType.InvoicePayed
        | "invoice.pending" -> SwapStatusType.InvoicePending
        | "invoice.settled" -> SwapStatusType.InvoiceSettled
        | "invoice.failedToPay" -> SwapStatusType.InvoiceFailedToPay

        | "channel.created" -> SwapStatusType.ChannelCreated

        | "transaction.failed" -> SwapStatusType.TxFailed
        | "transaction.mempool" -> SwapStatusType.TxMempool
        | "transaction.claimed" -> SwapStatusType.TxClaimed
        | "transaction.refunded" -> SwapStatusType.TxRefunded
        | "transaction.confirmed" -> SwapStatusType.TxConfirmed
        | _ -> SwapStatusType.Unknown

    and SwapStatusUpdate = {
      Id: SwapId
      Response: SwapStatusResponseData
      Network: Network
    }

  type Msg =
    | NewLoopOut of LoopOut
    | NewLoopIn of LoopIn
    | SwapUpdate of Data.SwapStatusUpdate
    | SetValidationError of id: SwapId * err: string
    member this.SwapId =
      match this with
      | NewLoopOut o -> o.Id
      | NewLoopIn i -> i.Id
      | SwapUpdate s -> s.Id
      | SetValidationError(id, _) -> id


  let streamId = StreamId("swap")

  // ------ event -----
  type Event =
    | KnownSwapAddedAgain of id: SwapId
    | NewLoopOutAdded of LoopOut
    | NewLoopInAdded of LoopIn
    | LoopErrored of id: SwapId * err: string
    | ClaimTxPublished of txid: uint256 * swapId: SwapId
    | SwapTxPublished of txid: uint256 * swapId: SwapId
    | ReceivedOffChainPayment of swapId: SwapId * paymentPreimage: PaymentPreimage
    with
    member this.Version =
      match this with
      | KnownSwapAddedAgain _
      | NewLoopOutAdded _
      | NewLoopInAdded _
      | LoopErrored _
      | ClaimTxPublished _
      | SwapTxPublished _
      | ReceivedOffChainPayment _
       -> 0

    member this.SwapId =
      match this with
      | KnownSwapAddedAgain swapId -> swapId
      | NewLoopOutAdded loopOut -> loopOut.Id
      | NewLoopInAdded loopIn -> loopIn.Id
      | LoopErrored (swapId, _) -> swapId
      | ClaimTxPublished(_txid, swapId) -> swapId
      | SwapTxPublished(_txId, swapId) -> swapId
      | ReceivedOffChainPayment(swapId, _) -> swapId

    member this.Type =
      match this with
      | KnownSwapAddedAgain _ -> "known_swap_added_again"
      | NewLoopOutAdded _ -> "new_loop_out_added"
      | NewLoopInAdded _ -> "new_loop_in_added"
      | LoopErrored _ -> "loop_errored"
      | ClaimTxPublished _ -> "claim_tx_published"
      | SwapTxPublished _ -> "swap_tx_published"
      | ReceivedOffChainPayment _ -> "received_offchain_payment"
    member this.ToEventSourcingEvent effectiveDate source : Event<Event> =
      {
        Event.Meta = { EventMeta.SourceName = source; EffectiveDate = effectiveDate }
        Type = (streamId.Value + "-" + this.Type) |> EventType.EventType
        Data = this
      }

  type Error =
    | TransactionError of Transactions.Error
    | UnExpectedError of exn
    | FailedToGetChangeAddress of string
    | UTXOProviderError of UTXOProviderError

  let serializer: Serializer<Event> = {
    Serializer.EventToBytes = JsonSerializer.SerializeToUtf8Bytes
    BytesToEvents =
      fun b ->
        try
          JsonSerializer.Deserialize(ReadOnlySpan<byte>.op_Implicit b)
          |> Ok
        with
        | ex ->
          $"Failed to deserialize event json\n%A{ex}"
          |> Error
  }

  // ------ deps -----
  type Deps = {
    Broadcaster: IBroadcaster
    FeeEstimator: IFeeEstimator
    UTXOProvider: IUTXOProvider
    LightningClient: INLoopLightningClient
    GetChangeAddress: GetChangeAddress
  }

  module private AsyncHelpers =
    let startPaymentRequest (lnClient: INLoopLightningClient) (loopOut: LoopOut) =
      let onSuccess =
        fun invoice ->
          let onSuccess (preimage) =
            (ReceivedOffChainPayment(loopOut.Id, preimage))
          Cmd.OfTask.perform
            (fun invoice -> lnClient.Offer(loopOut.PairId |> fst, invoice))
            (invoice)
            (onSuccess)
      let onError =
        (fun e -> LoopErrored(loopOut.Id, e) |> Cmd.ofMsg)
      PaymentRequest.Parse loopOut.Invoice
      |> ResultUtils.Result.either onSuccess onError

  // ----- aggregates ----

  let private enhanceEvents date source ((events, cmd) : Event list * Cmd<Event>) =
    events |> List.map(fun e -> e.ToEventSourcingEvent date source),
    cmd |> Cmd.map(fun e -> e.ToEventSourcingEvent date source)

  let executeCommand
    { Broadcaster = broadcaster; FeeEstimator = feeEstimator; UTXOProvider = utxoProvider;
      GetChangeAddress = getChangeAddress; LightningClient = lnClient  }
    (s: State)
    (cmd: Command<Msg>): Task<Result<Event<Event> list * Cmd<Event<Event>>, _>> =
    taskResult {
      try
        let { CommandMeta.EffectiveDate = effectiveDate; Source = source } = cmd.Meta
        let enhance = enhanceEvents effectiveDate source
        match cmd.Data with
        | NewLoopOut loopOut when s.OnGoing.Out |> Seq.exists(fun o -> o.Id = loopOut.Id) ->
          return ([KnownSwapAddedAgain loopOut.Id], Cmd.none) |> enhance
        | NewLoopOut loopOut ->
          return ([NewLoopOutAdded loopOut], Cmd.none) |> enhance
        | NewLoopIn loopIn when s.OnGoing.In |> Seq.exists(fun o -> o.Id = loopIn.Id) ->
          return ([KnownSwapAddedAgain loopIn.Id], Cmd.none) |> enhance
        | NewLoopIn loopIn ->
          return ([NewLoopInAdded loopIn], Cmd.none) |> enhance
        | SwapUpdate u when s.OnGoing.Out |> Seq.exists(fun o -> u.Id = o.Id) ->
          let ourSwap = s.OnGoing.Out.First(fun o -> u.Id = o.Id)
          if (u.Response.SwapStatus = ourSwap.Status) then
            return ([], Cmd.none)
          else
          match u.Response.SwapStatus with
          | SwapStatusType.TxMempool when not <| ourSwap.AcceptZeroConf ->
            return ([], Cmd.none)
          | SwapStatusType.TxMempool
          | SwapStatusType.TxConfirmed ->
            let (ourCryptoCode, counterPartyCryptoCode) = ourSwap.PairId
            let! feeRate =
              feeEstimator.Estimate(counterPartyCryptoCode)
            let lockupTx =
              u.Response.Transaction |> Option.defaultWith(fun () -> raise <| Exception("No Transaction in response"))
            let! claimTx =
              Transactions.createClaimTx
                (BitcoinAddress.Create(ourSwap.ClaimAddress, u.Network))
                (ourSwap.PrivateKey)
                (ourSwap.Preimage)
                (ourSwap.RedeemScript)
                (feeRate)
                (lockupTx.Tx)
                (u.Network)
              |> Result.mapError(TransactionError)
            do!
              broadcaster.BroadcastTx(claimTx, ourCryptoCode)
            let txid = claimTx.GetWitHash()
            let cmd = AsyncHelpers.startPaymentRequest lnClient ourSwap
            return ([ClaimTxPublished(txid, ourSwap.Id)], cmd) |> enhance
          | _ ->
            return ([], Cmd.none)
        | SwapUpdate u when s.OnGoing.In |> Seq.exists(fun o -> u.Id = o.Id) ->
          let ourSwap = s.OnGoing.In.First(fun o -> u.Id = o.Id)
          match u.Response.SwapStatus with
          | SwapStatusType.InvoiceSet ->
            // TODO: check confirmation?
            let (ourCryptoCode, counterPartyCryptoCode) = ourSwap.PairId
            let! utxos =
              utxoProvider.GetUTXOs(ourSwap.ExpectedAmount, ourCryptoCode)
              |> TaskResult.mapError(UTXOProviderError)
            let! feeRate =
              feeEstimator.Estimate(counterPartyCryptoCode)
            let! change =
              getChangeAddress.Invoke(ourCryptoCode)
              |> TaskResult.mapError(FailedToGetChangeAddress)
            let psbt =
              Transactions.createSwapPSBT
                (utxos)
                ourSwap.RedeemScript
                (ourSwap.ExpectedAmount)
                feeRate
                change
                ourSwap.TimeoutBlockHeight
                u.Network
              |> function | Ok x -> x | Error e -> failwithf "%A" e
            let! psbt = utxoProvider.SignSwapTxPSBT(psbt, ourCryptoCode)
            match psbt.TryFinalize() with
            | false, e ->
              return raise <| Exception(sprintf "%A" e)
            | true, _ ->
              let tx = psbt.ExtractTransaction()
              do! broadcaster.BroadcastTx(tx, ourCryptoCode)
              return ([SwapTxPublished(tx.GetWitHash(), ourSwap.Id)], Cmd.none) |> enhance
          | _ ->
          return ([], Cmd.none)
        | SwapUpdate u ->
          return raise <| Exception(sprintf "UnknownSwapId: %A"(u.Id))
        | SetValidationError(id, err) when
            s.OnGoing.In |> Seq.exists(fun i -> i.Id = id)  ||
            s.OnGoing.Out |> Seq.exists(fun i -> i.Id = id) ->
          return ([LoopErrored( id, err )], Cmd.none) |> enhance
        | SetValidationError(id, _err) ->
          return raise <| Exception(sprintf "UnknownSwapId: %A"id)
      with
      | ex ->
        return! UnExpectedError ex |> Error

    }

  let applyChanges
    (state: State) (event: Event) =
    match event with
    | KnownSwapAddedAgain _ ->
      state
    | ClaimTxPublished (txid, id) ->
      let newOuts =
        state.OnGoing.Out |> List.map(fun x -> if x.Id = id then { x with ClaimTransactionId = Some txid } else x)
      { state with OnGoing = { state.OnGoing with Out = newOuts } }
    | SwapTxPublished (txid, id) ->
      let newIns =
        state.OnGoing.In |> List.map(fun x -> if x.Id = id then { x with LockupTransactionId = Some txid } else x)
      { state with OnGoing = { state.OnGoing with In = newIns } }
    | NewLoopOutAdded loopOut ->
      { state with OnGoing = { state.OnGoing with Out = loopOut::state.OnGoing.Out } }
    | NewLoopInAdded loopIn ->
      { state with OnGoing = { state.OnGoing with In = loopIn::state.OnGoing.In } }
    | LoopErrored (id, err)->
      let newLoopIns =
        state.OnGoing.In |> List.map(fun s -> if s.Id = id then { s with Error = err } else s)
      let newLoopOuts =
        state.OnGoing.Out |> List.map(fun s -> if s.Id = id then { s with Error = err } else s)
      { state with OnGoing = { state.OnGoing with In = newLoopIns; Out = newLoopOuts } }
    | ReceivedOffChainPayment(swapId, preimage) ->
      let newLoopOuts =
        state.OnGoing.Out
        |> List.map(fun s ->
          if s.Id = swapId then { s with Preimage = preimage.ToByteArray() |> fun x -> uint256(x, false) } else
          s)
      { state with OnGoing = { state.OnGoing with Out = newLoopOuts } }

  type Aggregate = Aggregate<State, Msg, Event, Error, DateTime>
  type Handler = Handler<State, Msg, Event, Error, SwapId>

  let getAggregate deps: Aggregate = {
    Zero = State.Zero
    Exec = executeCommand deps
    Aggregate.Apply = applyChanges
    Filter = id
    Enrich = id
    SortBy = fun event ->
      event.Meta.EffectiveDate
  }

  let getRepository eventStoreUri =
    let store = eventStore eventStoreUri
    Repository.Create
      store
      serializer
      "swap"

  let getHandler aggr eventStoreUri =
    getRepository eventStoreUri
    |> Handler.Create aggr

  [<RequireQualifiedAccess>]
  module Projection =
    let swapProjection (handler: Handler) =
      fun (swapId: SwapId) (observationDate: ObservationDate) -> taskResult {
        let! events = handler.Replay swapId observationDate
        let _currentState =
          handler.Reconstitute events

        let _filter (e: Event<Event>) =
          e.Data.SwapId = swapId
        let swapOpt =
          events
          |> List.filter(fun e -> e.Data.SwapId = swapId)
          |> List.tryHead
        return
          match swapOpt with
          | Some _swap ->
            ()
          | None ->
            ()
      }
