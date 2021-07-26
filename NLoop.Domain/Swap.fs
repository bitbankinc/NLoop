namespace NLoop.Domain

open System
open System.Text.Json
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open FsToolkit.ErrorHandling
open NLoop.Domain.Utils.EventStore

[<RequireQualifiedAccess>]
/// List of ubiquitous languages
/// * SwapTx (LockupTx) ... On-Chain TX which offers funds with HTLC.
/// * ClaimTx ... TX to take funds from SwapTx in exchange of preimage
/// * RefundTx ... TX to take funds from SwapTx in case of the timeout.
/// * Offer ... the off-chain payment from us to counterparty. The preimage must be sufficient to claim SwapTx.
/// * Payment ... off-chain payment from counterparty to us.
module Swap =
  [<RequireQualifiedAccess>]
  type FinishedState =
    | Success
    /// Counterparty went offline. And we refunded our funds.
    | Refunded of uint256
    /// Counterparty gave us bogus msg. Swap did not start.
    | Errored of msg: string
  type State =
    | HasNotStarted
    | Out of blockHeight: BlockHeight * LoopOut
    | In of blockHeight: BlockHeight * LoopIn
    | Finished of FinishedState
    with
    static member Zero = HasNotStarted

  // ------ command -----

  module Data =
    type TxInfo = {
      TxId: uint256
      Tx: Transaction
      Eta: int option
    }
    and SwapStatusResponseData = {
      _Status: string
      Transaction: TxInfo option
      FailureReason: string option
    }
      with
      member this.SwapStatus =
        SwapStatusType.FromString(this._Status)


  [<Literal>]
  let entityType = "swap"

  type Command =
    // -- loop out --
    | NewLoopOut of height: BlockHeight * LoopOut
    | OffChainOfferResolve of paymentPreimage: PaymentPreimage

    // -- loop in --
    | NewLoopIn of height: BlockHeight * LoopIn

    // -- both
    | SwapUpdate of Data.SwapStatusResponseData
    | SetValidationError of err: string
    | NewBlock of height: BlockHeight * cryptoCode: SupportedCryptoCode

  // ------ event -----
  type Event =
    // -- loop out --
    | NewLoopOutAdded of  height: BlockHeight * loopIn: LoopOut
    | ClaimTxPublished of txid: uint256
    | OffChainOfferStarted of swapId: SwapId * pairId: PairId * invoice: PaymentRequest
    | OffChainOfferResolved of paymentPreimage: PaymentPreimage

    // -- loop in --
    | NewLoopInAdded of height: BlockHeight * loopIn: LoopIn
    // We do not use `Transaction` type just to make serializer happy.
    // if we stop using json serializer for serializing this event, we can use `Transaction` instead.
    // But it seems that just using string is much simpler.
    | SwapTxPublished of txHex: string
    | RefundTxPublished of txid: uint256

    // -- general --
    | NewTipReceived of BlockHeight

    | FinishedByError of id: SwapId * err: string
    | FinishedSuccessfully of id: SwapId
    | FinishedByRefund of id: SwapId
    | UnknownVersionEvent of version: byte * data: byte[]
    with
    member this.Version =
      match this with
      | NewLoopOutAdded _
      | ClaimTxPublished _
      | OffChainOfferStarted _
      | OffChainOfferResolved _

      | NewLoopInAdded _
      | SwapTxPublished _
      | RefundTxPublished _

      | NewTipReceived _
      | FinishedByError _

      | FinishedSuccessfully _
      | FinishedByRefund _
       -> 0uy
      | UnknownVersionEvent (v, _) -> v

    member this.Type =
      match this with
      | NewLoopOutAdded _ -> "new_loop_out_added"
      | ClaimTxPublished _ -> "claim_tx_published"
      | OffChainOfferStarted _ -> "offchain_offer_started"
      | OffChainOfferResolved _ -> "offchain_offer_resolved"

      | NewLoopInAdded _ -> "new_loop_in_added"
      | SwapTxPublished _ -> "swap_tx_published"
      | RefundTxPublished _ -> "refund_tx_published"

      | NewTipReceived _ -> "new_tip_received"

      | FinishedByError _ -> "finished_by_error"
      | FinishedSuccessfully _ -> "finished_successfully"
      | FinishedByRefund _ -> "finished_by_refund"
      | UnknownVersionEvent _ -> "unknown_version_event"
    member this.ToEventSourcingEvent effectiveDate source : Event<Event> =
      {
        Event.Meta = { EventMeta.SourceName = source; EffectiveDate = effectiveDate }
        Type = (entityType + "-" + this.Type) |> EventType.EventType
        Data = this
      }

  let KnownEventVersions = [|0uy|]

  type Error =
    | TransactionError of string
    | UnExpectedError of exn
    | FailedToGetAddress of string
    | UTXOProviderError of UTXOProviderError
    | InputError of string

  let inline private expectTxError (txName: string) (r: Result<_, Transactions.Error>) =
    r |> Result.mapError(fun e -> $"Error while creating {txName}: {e.Message}" |> TransactionError)

  let inline private expectInputError(r: Result<_, string>) =
    r |> Result.mapError InputError

  let private jsonConverterOpts =
    let o = JsonSerializerOptions()
    o.AddNLoopJsonConverters()
    o
  let serializer : Serializer<Event> = {
    Serializer.EventToBytes = fun (e: Event) ->
      let v = e.Version |> uint8 |> Array.singleton
      let b =
        match e with
        | UnknownVersionEvent (_, b) ->
          b
        | e -> JsonSerializer.SerializeToUtf8Bytes(e, jsonConverterOpts)
      Array.concat (seq [v; b])
    BytesToEvents =
      fun b ->
        try
          let e =
            match b.[0] with
            | 0uy ->
              let e = JsonSerializer.Deserialize(ReadOnlySpan<byte>.op_Implicit b.[1..], jsonConverterOpts)
              assert(e |> function | UnknownVersionEvent _ -> false | _ -> true)
              e
            | v ->
              UnknownVersionEvent(v, b.[1..])
          e |> Ok
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
    GetChangeAddress: GetAddress
    GetRefundAddress: GetAddress
  }

  // ----- aggregates ----


  let private enhanceEvents date source (events: Event list) =
    events |> List.map(fun e -> e.ToEventSourcingEvent date source)


  let executeCommand
    { Broadcaster = broadcaster; FeeEstimator = feeEstimator; UTXOProvider = utxoProvider;
      GetChangeAddress = getChangeAddress; GetRefundAddress = getRefundAddress }
    (s: State)
    (cmd: ESCommand<Command>): Task<Result<Event<Event> list, _>> =
    taskResult {
      try
        let { CommandMeta.EffectiveDate = effectiveDate; Source = source } = cmd.Meta
        let enhance = enhanceEvents effectiveDate source
        let checkHeight (height: BlockHeight) (oldHeight: BlockHeight) =
          if height.Value > oldHeight.Value then
            [NewTipReceived(height)] |> enhance |> Ok
          else
            [] |> Ok

        match cmd.Data, s with
        // --- loop out ---
        | NewLoopOut (h, loopOut), HasNotStarted ->
          do! loopOut.Validate() |> expectInputError
          let invoice =
            loopOut.Invoice
            |> PaymentRequest.Parse
            |> ResultUtils.Result.deref
          return
            [NewLoopOutAdded(h, loopOut); OffChainOfferStarted(loopOut.Id, loopOut.PairId, invoice) ]
            |> enhance
        | OffChainOfferResolve pp, Out(_, loopOut) ->
          return [OffChainOfferResolved pp; FinishedSuccessfully loopOut.Id] |> enhance
        | SwapUpdate u, Out(_height, loopOut) ->
          if (u.SwapStatus = loopOut.Status) then
            return []
          else
          match u.SwapStatus with
          | SwapStatusType.TxMempool when not <| loopOut.AcceptZeroConf ->
            return []
          | SwapStatusType.TxMempool
          | SwapStatusType.TxConfirmed ->
            let struct (_, counterPartyCryptoCode) = loopOut.PairId
            let! feeRate =
              feeEstimator.Estimate(counterPartyCryptoCode)
            let lockupTx =
              u.Transaction |> Option.defaultWith(fun () -> raise <| Exception("No Transaction in response"))
            let! claimTx =
              Transactions.createClaimTx
                (BitcoinAddress.Create(loopOut.ClaimAddress, loopOut.OurNetwork))
                (loopOut.ClaimKey)
                (loopOut.Preimage)
                (loopOut.RedeemScript)
                (feeRate)
                (lockupTx.Tx)
                (loopOut.OurNetwork)
              |> expectTxError "claim tx"
            do!
              broadcaster.BroadcastTx(claimTx, counterPartyCryptoCode)
            let txid = claimTx.GetWitHash()
            return [ClaimTxPublished(txid);] |> enhance
          | SwapStatusType.SwapExpired ->
            let reason = u.FailureReason |> Option.defaultValue ""
            return [FinishedByError(loopOut.Id, $"Swap expired (Reason: %s{reason})");] |> enhance
          | _ ->
            return []

        // --- loop in ---
        | NewLoopIn(h, loopIn), HasNotStarted ->
          do! loopIn.Validate() |> expectInputError
          return [NewLoopInAdded(h, loopIn)] |> enhance
        | SwapUpdate u, In(_height, loopIn) ->
          match u.SwapStatus with
          | SwapStatusType.InvoiceSet ->
            let (struct (_ourCryptoCode, counterPartyCryptoCode)) = loopIn.PairId
            let! utxos =
              utxoProvider.GetUTXOs(loopIn.ExpectedAmount, counterPartyCryptoCode)
              |> TaskResult.mapError(UTXOProviderError)
            let! feeRate =
              feeEstimator.Estimate(counterPartyCryptoCode)
            let! change =
              getChangeAddress.Invoke(counterPartyCryptoCode)
              |> TaskResult.mapError(FailedToGetAddress)
            let psbt =
              Transactions.createSwapPSBT
                (utxos)
                loopIn.RedeemScript
                (loopIn.ExpectedAmount)
                feeRate
                change
                loopIn.TheirNetwork
              |> function | Ok x -> x | Error e -> failwithf "%A" e
            let! psbt = utxoProvider.SignSwapTxPSBT(psbt, counterPartyCryptoCode)
            match psbt.TryFinalize() with
            | false, e ->
              return raise <| Exception(sprintf "%A" (e |> Seq.toList))
            | true, _ ->
              let tx = psbt.ExtractTransaction()
              do! broadcaster.BroadcastTx(tx, counterPartyCryptoCode)
              return [SwapTxPublished(tx.ToHex())] |> enhance
          | SwapStatusType.TxConfirmed
          | SwapStatusType.InvoicePayed ->
            // Ball is on their side. Just wait till they claim their on-chain share.
            return []
          | SwapStatusType.TxClaimed ->
            // boltz server has happily claimed their on-chain share.
            return [FinishedSuccessfully(loopIn.Id)] |> enhance
          | SwapStatusType.InvoiceFailedToPay ->
            // The counterparty says that they have failed to receive preimage before timeout. This should never happen.
            // But even if it does, we will just reclaim the swap tx when the timeout height has reached.
            // So nothing to do here.
            return []
          | SwapStatusType.SwapExpired ->
            // This means we have not send SwapTx. Or they did not recognize it.
            // This can be caused only by our bug.
            // (e.g. boltz-server did not recognize our swap script.)
            // So just ignore it.
            return []
          | _ ->
            return []

        // --- ---

        | SetValidationError(err), Out(_, { Id = swapId })
        | SetValidationError(err), In (_ , { Id = swapId }) ->
          return [FinishedByError( swapId, err )] |> enhance
        | NewBlock (height, cc), Out(oldHeight, loopOut) when let struct (ourCC,_ ) = loopOut.PairId in ourCC = cc ->
            return! (height, oldHeight) ||> checkHeight
        | NewBlock (height, cc), In(oldHeight, loopIn) when let struct (_, theirCC) = loopIn.PairId in theirCC = cc ->
          let! events = (height, oldHeight) ||> checkHeight
          if loopIn.TimeoutBlockHeight <= height then
            let struct(_ourCC, theirCC) = loopIn.PairId
            let! refundAddress =
              getRefundAddress.Invoke(theirCC)
              |> TaskResult.mapError(FailedToGetAddress)
            let! fee =
              feeEstimator.Estimate(theirCC)
            let! refundTx =
              Transactions.createRefundTx
                (loopIn.LockupTransactionHex.Value)
                (loopIn.RedeemScript)
                fee
                (refundAddress)
                (loopIn.RefundPrivateKey)
                (loopIn.TimeoutBlockHeight)
                (loopIn.TheirNetwork)
              |> expectTxError "refund tx"

            do! broadcaster.BroadcastTx(refundTx, theirCC)
            let additionalEvents =
              [RefundTxPublished(refundTx.GetWitHash()); FinishedByRefund loopIn.Id]
              |> enhance
            return events @ additionalEvents
          else
            return events
        | _, Finished _ ->
          return []
        | x, s ->
          return raise <| Exception($"Unexpected Command \n{x} \n\nWhile in the state\n{s}")
      with
      | ex ->
        return! UnExpectedError ex |> Error
    }

  let applyChanges
    (state: State) (event: Event) =
    match event, state with
    | NewLoopOutAdded(h, x), HasNotStarted ->
      Out (h, x)
    | ClaimTxPublished (txid), Out(h, x) ->
      Out (h, { x with ClaimTransactionId = Some txid })
    | OffChainOfferResolved(preimage), Out(h, x) ->
      Out(h, { x with Preimage = preimage })

    | NewLoopInAdded(h, x), HasNotStarted ->
      In (h, x)
    | SwapTxPublished (tx), In(h, x) ->
      In (h, { x with LockupTransactionHex = Some(tx) })
    | RefundTxPublished txid, In(h, x) ->
      In(h, { x with RefundTransactionId = Some txid })

    | FinishedByError (_, err), In _ ->
      Finished(FinishedState.Errored(err))
    | FinishedByError (_, err), Out _ ->
      Finished(FinishedState.Errored(err))
    | FinishedSuccessfully _, Out _
    | FinishedSuccessfully _, In _ ->
      Finished(FinishedState.Success)
    | FinishedByRefund _, In (_h, { RefundTransactionId = Some txid }) ->
      Finished(FinishedState.Refunded(txid))

    | NewTipReceived h, Out(_, x) ->
      Out(h, x)
    | NewTipReceived h, In(_, x) ->
      In(h, x)
    | _, x -> x

  type Aggregate = Aggregate<State, Command, Event, Error, DateTime * string>
  type Handler = Handler<State, Command, Event, Error, SwapId>

  let getAggregate deps: Aggregate = {
    Zero = State.Zero
    Exec = executeCommand deps
    Aggregate.Apply = applyChanges
    Filter = id
    Enrich = id
    SortBy = fun event ->
      event.Meta.EffectiveDate.Value, event.Data.Type
  }

  let getRepository eventStoreUri =
    let store = eventStore eventStoreUri
    Repository.Create
      store
      serializer
      entityType

  let getHandler aggr eventStoreUri =
    getRepository eventStoreUri
    |> Handler.Create aggr

