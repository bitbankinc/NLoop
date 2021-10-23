namespace NLoop.Domain

open System
open System.Text.Json
open System.Threading.Tasks
open System.Threading.Tasks
open DotNetLightning.Utils
open FSharp.Control.Tasks
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
/// * ClaimTx ... TX to take funds from SwapTx in exchange of preimage. a.k.a "sweep" (loop out)
/// * RefundTx ... TX to take funds from SwapTx in case of the timeout (loop in).
/// * SuccessTx ... counterparty's claim tx (loop in)
/// * Offer ... the off-chain payment from us to counterparty. The preimage must be sufficient for us to claim the SwapTx.
/// * Payment ... off-chain payment from counterparty to us.
module Swap =
  [<RequireQualifiedAccess>]
  type FinishedState =
    | Success
    /// Counterparty went offline. And we refunded our funds.
    | Refunded of uint256
    /// Counterparty gave us bogus msg. Swap did not start.
    | Errored of msg: string
    /// Counterparty did not publish the swap tx (lockup tx) in loopout, so the swap is canceled.
    | Timeout of msg: string

  [<Struct>]
  type Category =
    | In
    | Out

  type State =
    | HasNotStarted
    | Out of blockHeight: BlockHeight * LoopOut
    | In of blockHeight: BlockHeight * LoopIn
    | Finished of finalCost: SwapCost * FinishedState
    with
    static member Zero = HasNotStarted

    member this.Cost =
      match this with
      | HasNotStarted _ -> SwapCost.Zero
      | Out(_, { Cost = cost })
      | In(_, { Cost = cost })
      | Finished(cost, _) -> cost

  // ------ command -----

  type BogusResponseError =
    | NoTx
    | TxDoesNotPayToClaimAddress
    | PaymentAmountMismatch of expected: Money * actual: Money
    with
    member this.Message =
      match this with
      | NoTx _ -> "no swap tx in response"
      | TxDoesNotPayToClaimAddress -> "Swap TX does not pay to the address we have specified"
      | PaymentAmountMismatch (e, a) -> $"Swap tx output amount mismatch. (expected: {e}, actual: {a})"
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

      member this.ValidateAndGetTx(loopOut: LoopOut ) =
        result {
          let! swapTx =
            this.Transaction
            |> Option.map(fun txInfo -> txInfo.Tx)
            |>
              function
              | Some x -> Ok x
              | None -> Error BogusResponseError.NoTx

          let! swapTxAmount =
            swapTx.Outputs
            |> Seq.tryPick(fun o ->
              if o.ScriptPubKey.Equals(loopOut.RedeemScript.WitHash.ScriptPubKey) then
                Some o.Value
              else
                None
              )
            |>
              function
              | Some x -> Ok x
              | None ->
                Error BogusResponseError.TxDoesNotPayToClaimAddress

          return!
            if swapTxAmount <> loopOut.OnChainAmount then
              Error (BogusResponseError.PaymentAmountMismatch(loopOut.OnChainAmount, swapTxAmount))
            else
              Ok swapTx
        }

  [<Literal>]
  let entityType = "swap"

  [<AutoOpen>]
  module private Constants =
    let MinPreimageRevealDelta = BlockHeightOffset32(20u)

    let DefaultSweepConfTarget = BlockHeightOffset32 9u

    let DefaultSweepConfTargetDelta = BlockHeightOffset32 18u

  type LoopOutParams = {
    MaxPrepayFee: Money
    MaxPaymentFee: Money
    OutgoingChanIds: ShortChannelId []
    Height: BlockHeight
  }

  type PayInvoiceResult = {
    RoutingFee: LNMoney
    AmountPayed: LNMoney
  }

  type Command =
    // -- loop out --
    | NewLoopOut of LoopOutParams * LoopOut
    | OffChainOfferResolve of PayInvoiceResult

    // -- loop in --
    | NewLoopIn of height: BlockHeight * LoopIn
    | CommitReceivedOffChainPayment of amt: Money

    // -- both
    | SwapUpdate of Data.SwapStatusResponseData
    | MarkAsErrored of err: string
    | NewBlock of height: BlockHeight * block: Block * cryptoCode: SupportedCryptoCode

  type PayInvoiceParams = {
    MaxFee: Money
    OutgoingChannelIds: ShortChannelId []
  }
  // ------ event -----
  type Event =
    // -- loop out --
    | NewLoopOutAdded of height: BlockHeight * loopOut: LoopOut
    | OffChainOfferStarted of swapId: SwapId * pairId: PairId * invoice: PaymentRequest * payInvoiceParams: PayInvoiceParams
    | ClaimTxPublished of txid: uint256
    | SweepTxConfirmed of txid: uint256 * sweepAmount: Money
    | PrePayFinished of PayInvoiceResult
    | OffchainOfferResolved of PayInvoiceResult
    | TheirSwapTxPublished of txHex: string

    // -- loop in --
    | NewLoopInAdded of height: BlockHeight * loopIn: LoopIn
    // We do not use `Transaction` type just to make serializer happy.
    // if we stop using json serializer for serializing this event, we can use `Transaction` instead.
    // But it seems that just using string is much simpler.
    | OurSwapTxPublished of fee: Money * txHex: string
    | OurSwapTxConfirmed of txid: uint256 * htlcOutIndex: uint
    | RefundTxPublished of txid: uint256
    | RefundTxConfirmed of fee: Money * txid: uint256
    | SuccessTxConfirmed of htlcValue: Money * txid: uint256
    | OffChainPaymentReceived of amt: Money

    // -- general --
    | NewTipReceived of BlockHeight

    | FinishedByError of id: SwapId * err: string
    | FinishedSuccessfully of id: SwapId
    | FinishedByRefund of id: SwapId
    | FinishedByTimeout of id: SwapId * reason: string
    | UnknownTagEvent of tag: uint16 * data: byte[]
    with
    member this.EventTag =
      match this with
      | NewLoopOutAdded _ -> 0us
      | ClaimTxPublished _ -> 1us
      | OffChainOfferStarted _ -> 2us
      | OffchainOfferResolved _ -> 3us
      | SweepTxConfirmed _ -> 4us
      | PrePayFinished _ -> 5us
      | TheirSwapTxPublished _ -> 6us
      | OffChainPaymentReceived _ -> 7us

      | NewLoopInAdded _ -> 256us + 0us
      | OurSwapTxPublished _ -> 256us + 1us
      | OurSwapTxConfirmed _ -> 256us + 2us
      | RefundTxPublished _-> 256us + 3us
      | RefundTxConfirmed _ -> 256us + 4us
      | SuccessTxConfirmed _ -> 256us + 5us

      | NewTipReceived _ -> 512us + 0us

      | FinishedSuccessfully _ -> 1024us + 0us
      | FinishedByRefund _ -> 1024us + 1us
      | FinishedByError _ -> 1024us + 2us
      | FinishedByTimeout _ -> 1024us + 3us

      | UnknownTagEvent (t, _) -> t

    member this.Type =
      match this with
      | NewLoopOutAdded _ -> "new_loop_out_added"
      | ClaimTxPublished _ -> "claim_tx_published"
      | OffChainOfferStarted _ -> "offchain_offer_started"
      | OffchainOfferResolved _ -> "offchain_offer_resolved"
      | SweepTxConfirmed _ -> "sweep_tx_confirmed"
      | PrePayFinished _ -> "prepay_finished"
      | TheirSwapTxPublished _ -> "their_swap_tx_published"
      | OffChainPaymentReceived _ -> "offchain_payment_received"

      | NewLoopInAdded _ -> "new_loop_in_added"
      | OurSwapTxPublished _ -> "our_swap_tx_published"
      | OurSwapTxConfirmed _ -> "our_swap_tx_confirmed"
      | RefundTxPublished _ -> "refund_tx_published"
      | RefundTxConfirmed _ -> "refund_tx_confirmed"
      | SuccessTxConfirmed _ -> "success_tx_confirmed"

      | NewTipReceived _ -> "new_tip_received"

      | FinishedSuccessfully _ -> "finished_successfully"
      | FinishedByRefund _ -> "finished_by_refund"
      | FinishedByError _ -> "finished_by_error"
      | FinishedByTimeout _ -> "finished_by_timeout"

      | UnknownTagEvent _ -> "unknown_version_event"
    member this.ToEventSourcingEvent effectiveDate source : ESEvent<Event> =
      {
        ESEvent.Meta = { EventMeta.SourceName = source; EffectiveDate = effectiveDate }
        Type = (entityType + "-" + this.Type) |> EventType.EventType
        Data = this
      }

  type Error =
    | TransactionError of string
    | UnExpectedError of exn
    | FailedToGetAddress of string
    | UTXOProviderError of UTXOProviderError
    | InputError of string
    | CanNotSafelyRevealPreimage
    | CounterPartyReturnedBogusResponse of BogusResponseError
    | BogusSwapTransaction of msg: string

  let inline private expectTxError (txName: string) (r: Result<_, Transactions.Error>) =
    r |> Result.mapError(fun e -> $"Error while creating {txName}: {e.Message}" |> TransactionError)

  let inline private expectBogusSwapTx(r: Result<_, string>) =
    r |> Result.mapError(fun msg -> $"SwapTx is Bogus! This should never happen: {msg}" |> BogusSwapTransaction)

  let inline private expectInputError(r: Result<_, string>) =
    r |> Result.mapError InputError

  let private jsonConverterOpts =
    let o = JsonSerializerOptions()
    o.AddNLoopJsonConverters()
    o
  let serializer : Serializer<Event> = {
    Serializer.EventToBytes = fun (e: Event) ->
      let v = e.EventTag |> fun t -> Utils.ToBytes(t, false)
      let b =
        match e with
        | UnknownTagEvent (_, b) ->
          b
        | e -> JsonSerializer.SerializeToUtf8Bytes(e, jsonConverterOpts)
      Array.concat (seq [v; b])
    BytesToEvents =
      fun b ->
        try
          let e =
            match Utils.ToUInt16(b.[0..1], false) with
            | 0us | 1us | 2us | 3us | 4us | 5us | 6us | 7us
            | 256us | 257us | 258us | 259us | 260us | 261us
            | 512us
            | 1024us | 1025us | 1026us | 1027us ->
              JsonSerializer.Deserialize(ReadOnlySpan<byte>.op_Implicit b.[2..], jsonConverterOpts)
            | v ->
              UnknownTagEvent(v, b.[2..])
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
    /// Used for pre-paying miner fee.
    /// This is not for paying an actual swap invoice, since we cannot expect it to get finished immediately.
    PayInvoice: Network -> PayInvoiceParams -> PaymentRequest -> Task<PayInvoiceResult>
  }

  // ----- aggregates ----


  let private enhanceEvents date source (events: Event list) =
    events |> List.map(fun e -> e.ToEventSourcingEvent date source)

  /// Returns None in case we don't have to do anything.
  /// Otherwise returns txid for sweep tx.
  let private sweepOrBump
    { Deps.FeeEstimator = feeEstimator; Broadcaster = broadcaster }
    (height: BlockHeight)
    (lockupTx: Transaction)
    (loopOut: LoopOut): Task<Result<_ option, Error>> = taskResult {
      let struct (baseAsset, _quoteAsset) = loopOut.PairId
      let! feeRate =
        // the block confirmation target for fee estimation.
        let confTarget =
          let remainingBlocks = loopOut.TimeoutBlockHeight - height
          // iI we have come too close to the expiration height, we will use DefaultSweepConfTarget
          // unless the user-provided one is shorter than the default.
          if remainingBlocks <= DefaultSweepConfTargetDelta && loopOut.SweepConfTarget > DefaultSweepConfTarget then
            DefaultSweepConfTarget
          else
            loopOut.SweepConfTarget
        feeEstimator.Estimate confTarget baseAsset
      let getClaimTx feeRate: Result<Transaction, Error> =
        Transactions.createClaimTx
          (BitcoinAddress.Create(loopOut.ClaimAddress, loopOut.BaseAssetNetwork))
          loopOut.ClaimKey
          loopOut.Preimage
          loopOut.RedeemScript
          feeRate
          lockupTx
          loopOut.BaseAssetNetwork
        |> expectTxError "claim tx"
      let! claimTx = getClaimTx feeRate
      let! maybeClaimTxToPublish =
        if feeRate.GetFee(claimTx) <= loopOut.MaxMinerFee  then
          claimTx |> Some |> Ok
        else
          // requested fee exceeds our our maximum fee.
          if loopOut.ClaimTransactionId.IsSome then
            // if the preimage is already revealed, we have no choice to bump the fee
            // to our possible maximum value.
            getClaimTx (FeeRate(loopOut.MaxMinerFee, claimTx.GetVirtualSize()))
            |> Result.map(Some)
          else
            // otherwise, we should not do anything.
            None |> Ok

      match maybeClaimTxToPublish with
      | Some claimTx ->
        do!
          broadcaster.BroadcastTx(claimTx, baseAsset)
      | None -> ()
      return
        maybeClaimTxToPublish |> Option.map(fun t -> t.GetHash())
    }

  let executeCommand
    ({ Broadcaster = broadcaster; FeeEstimator = feeEstimator; UTXOProvider = utxoProvider;
       GetChangeAddress = getChangeAddress; GetRefundAddress = getRefundAddress; PayInvoice = payInvoice
     } as deps)
    (s: State)
    (cmd: ESCommand<Command>): Task<Result<ESEvent<Event> list, _>> =
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
        | NewLoopOut({ Height = h } as p, loopOut), HasNotStarted ->
          do! loopOut.Validate() |> expectInputError
          let! additionalEvents =
            if loopOut.PrepayInvoice |> String.IsNullOrEmpty |> not then
              let prepaymentParams =
                { PayInvoiceParams.MaxFee =  p.MaxPrepayFee
                  OutgoingChannelIds = p.OutgoingChanIds }
              loopOut.PrepayInvoice
              |> PaymentRequest.Parse
              |> ResultUtils.Result.deref
              |> payInvoice
                   loopOut.QuoteAssetNetwork
                   prepaymentParams
              |> Task.map(PrePayFinished >> List.singleton >> Ok)
            else
              Task.FromResult(Ok [])
          let invoice =
            loopOut.Invoice
            |> PaymentRequest.Parse
            |> ResultUtils.Result.deref
          do! invoice.AmountValue |> function | Some _ -> Ok() | None -> Error(Error.InputError($"invoice has no amount specified"))
          return
            [
              NewLoopOutAdded(h, loopOut)
              yield! additionalEvents
              let paymentParams = {
                MaxFee = p.MaxPrepayFee
                OutgoingChannelIds = p.OutgoingChanIds
              }
              OffChainOfferStarted(loopOut.Id, loopOut.PairId, invoice, paymentParams)
            ]
            |> enhance
        | OffChainOfferResolve payInvoiceResult, Out(_h, loopOut) ->
          return [
            OffchainOfferResolved payInvoiceResult
            if loopOut.IsClaimTxConfirmed then
              FinishedSuccessfully(loopOut.Id)
          ] |> enhance
        | SwapUpdate u, Out(height, loopOut) ->
          if (u.SwapStatus = loopOut.Status) then
            return []
          else
          match u.SwapStatus with
          | SwapStatusType.TxMempool when not <| loopOut.AcceptZeroConf ->
            return []
          | SwapStatusType.TxMempool
          | SwapStatusType.TxConfirmed ->

            let! swapTx =
              u.ValidateAndGetTx(loopOut)
              |> Result.mapError(Error.CounterPartyReturnedBogusResponse)

            let e = TheirSwapTxPublished(swapTx.ToHex())
            match! sweepOrBump deps height swapTx loopOut with
            | Some txid ->
              return [e; ClaimTxPublished(txid);] |> enhance
            | None ->
              return [e] |> enhance

          | SwapStatusType.SwapExpired ->
            let reason =
              u.FailureReason
              |> Option.defaultValue "Counterparty claimed that the loop out is expired but we don't know the exact reason."
            return [FinishedByTimeout(loopOut.Id, $"Swap expired (Reason: %s{reason})");] |> enhance
          | _ ->
            return []

        // --- loop in ---
        | NewLoopIn(h, loopIn), HasNotStarted ->
          do! loopIn.Validate() |> expectInputError
          return [NewLoopInAdded(h, loopIn)] |> enhance
        | SwapUpdate u, In(_height, loopIn) ->
          match u.SwapStatus with
          | SwapStatusType.InvoiceSet ->
            let struct (baseAsset, _) = loopIn.PairId
            let! utxos =
              utxoProvider.GetUTXOs(loopIn.ExpectedAmount, baseAsset)
              |> TaskResult.mapError(UTXOProviderError)
            let! feeRate =
              feeEstimator.Estimate
                loopIn.HTLCConfTarget
                baseAsset
            let! change =
              getChangeAddress.Invoke(baseAsset)
              |> TaskResult.mapError(FailedToGetAddress)
            let psbt =
              Transactions.createSwapPSBT
                utxos
                loopIn.RedeemScript
                loopIn.ExpectedAmount
                feeRate
                change
                loopIn.QuoteAssetNetwork
              |> function | Ok x -> x | Error e -> failwith $"%A{e}"
            let! psbt = utxoProvider.SignSwapTxPSBT(psbt, baseAsset)
            match psbt.TryFinalize() with
            | false, e ->
              return raise <| Exception $"%A{e |> Seq.toList}"
            | true, _ ->
              let tx = psbt.ExtractTransaction()
              let fee = feeRate.GetFee(tx)
              if fee < loopIn.MaxMinerFee then
                // we give up executing the swap here rather than dealing with the fee-market nitty-gritty.
                return [
                  let msg = $"OnChain FeeRate is too high. (actual fee: {fee}. Our maximum: {loopIn.MaxMinerFee})"
                  FinishedByError(loopIn.Id, msg)
                ] |> enhance
              else
                do! broadcaster.BroadcastTx(tx, baseAsset)
                return [
                  OurSwapTxPublished(fee, tx.ToHex())
                ] |> enhance
          | SwapStatusType.TxConfirmed
          | SwapStatusType.InvoicePayed ->
            // Ball is on their side. Just wait till they claim their on-chain share.
            return []
          | SwapStatusType.TxClaimed ->
            return [] |> enhance
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
        | CommitReceivedOffChainPayment amt, In _ ->
          return [ OffChainPaymentReceived(amt) ] |> enhance

        // --- ---

        | MarkAsErrored(err), Out(_, { Id = swapId })
        | MarkAsErrored(err), In (_ , { Id = swapId }) ->
          return [FinishedByError( swapId, err )] |> enhance
        | NewBlock (height, block, cc), Out(oldHeight, ({ ClaimTransactionId = maybePrevClaimTxId; PairId = struct(baseAsset, _); TimeoutBlockHeight = timeout } as loopOut))
          when baseAsset = cc ->
            let! events = (height, oldHeight) ||> checkHeight
            match maybePrevClaimTxId
                  |> Option.bind(fun txid -> block.Transactions |> Seq.tryFind(fun t -> t.GetHash() = txid)) with
            | Some sweepTx ->
              let sweepTxAmount =
                sweepTx.Outputs
                |> Seq.pick(fun o ->
                  if o.ScriptPubKey.GetDestinationAddress(loopOut.BaseAssetNetwork).ToString() = loopOut.ClaimAddress then
                    Some(o.Value)
                  else
                    None
                  )
              // Our sweep tx is confirmed, this swap is finished!
              let additionalEvents = [
                SweepTxConfirmed(sweepTx.GetHash(), sweepTxAmount)
                // We do not mark it as finished until the counterparty receives their share.
                // This is because we don't know how much we have payed as off-chain routing fee until counterparty
                // receives. (Usually they receive way before the sweep tx is confirmed so it is very rare case that
                // this will be false.)
                if loopOut.IsOffchainOfferResolved then
                  FinishedSuccessfully(loopOut.Id)
              ]
              return events @ (additionalEvents |> enhance)
            | _ ->
            // we have to create or bump the sweep tx.
            let! additionalEvents = taskResult {
              let remainingBlocks = timeout - height
              let haveWeRevealedThePreimage = maybePrevClaimTxId.IsSome
              // If we have not revealed the preimage, and we don't have time left to sweep the swap,
              // we abandon the swap because we can no longer sweep on the success path (without potentially having to
              // compete with server's timeout tx.) and we have not had any coins pulled off-chain
              if remainingBlocks <= MinPreimageRevealDelta && not <| haveWeRevealedThePreimage then
                let msg =
                  $"We reached a timeout height (timeout: {timeout}) - (current height: {height}) < (minimum_delta: {MinPreimageRevealDelta})" +
                  "That means Preimage can no longer safely revealed, so we will give up the swap"
                return [FinishedByTimeout(loopOut.Id, msg)] |> enhance
              else
                match loopOut.LockupTransaction with
                | None ->
                  // When we don't know lockup tx (a.k.a. swap tx), there is no way we can claim our share.
                  return []
                | Some tx ->
                  match! sweepOrBump deps height tx loopOut with
                  | Some txid ->
                    return [ClaimTxPublished(txid);] |> enhance
                  | None ->
                    return []
              }
            return events @ additionalEvents

        | NewBlock (height, block, cc), In(oldHeight, loopIn) when let struct (_, quoteAsset) = loopIn.PairId in quoteAsset = cc ->
          let! events = (height, oldHeight) ||> checkHeight
          let! maybeSwapTxConfirmedEvent = result {
            let maybeSwapTxInBlock =
              loopIn.LockupTransactionHex
              |> Option.bind(fun swapTxHex ->
                let swapTx = Transaction.Parse(swapTxHex, loopIn.QuoteAssetNetwork)
                block.Transactions |> Seq.tryFind(fun tx -> tx.GetHash() = swapTx.GetHash())
              )
            match maybeSwapTxInBlock with
            | Some tx ->
              let! index =
                tx.ValidateOurSwapTxOut(loopIn.RedeemScript, loopIn.ExpectedAmount)
                |> expectBogusSwapTx
              return [
                OurSwapTxConfirmed(tx.GetHash(), index)
              ] |> enhance
            | None ->
              return []
            }
          let maybeSpendTx =
            loopIn.LockupTransactionOutPoint
            |> Option.bind(fun (txid, vOut) ->
              block.Transactions
              |> Seq.tryPick(fun t ->
                t.Inputs
                |> Seq.tryPick(fun i ->
                  if i.PrevOut.Hash = txid && i.PrevOut.N = vOut then
                    Some(i, t)
                  else
                    None
                )
              )
            )

          let maybeSpendTxEvent =
            maybeSpendTx
            |> Option.map(fun(txin, tx) ->
              assert loopIn.LockupTransactionHex.IsSome
              let swapTx = Transaction.Parse(loopIn.LockupTransactionHex.Value, loopIn.QuoteAssetNetwork)
              if (txin.WitScript |> Scripts.isSuccessWitness) then
                // confirmed tx is their claim tx.
                [
                  let htlcOut =
                    swapTx.Outputs.[txin.PrevOut.N]
                  SuccessTxConfirmed(htlcOut.Value, tx.GetHash())
                  FinishedSuccessfully(loopIn.Id)
                ] |> enhance
              else
                // confirmed tx is our refund tx.
                let refundTxFee =
                  let c = swapTx.Outputs.AsCoins() |> Seq.cast |> Seq.toArray
                  tx.GetFee(c)
                [ RefundTxConfirmed(refundTxFee, tx.GetHash()); FinishedByRefund loopIn.Id ] |> enhance
            )
            |> Option.toList
            |> List.concat

          let! publishEvent = taskResult {
            match maybeSpendTx with
            | None when loopIn.TimeoutBlockHeight <= height ->
              let struct(_, quoteAsset) = loopIn.PairId
              let! refundAddress =
                getRefundAddress.Invoke(quoteAsset)
                |> TaskResult.mapError(FailedToGetAddress)
              let! fee =
                feeEstimator.Estimate loopIn.HTLCConfTarget quoteAsset
              let! refundTx =
                Transactions.createRefundTx
                  loopIn.LockupTransactionHex.Value
                  loopIn.RedeemScript
                  fee
                  refundAddress
                  loopIn.RefundPrivateKey
                  loopIn.TimeoutBlockHeight
                  loopIn.QuoteAssetNetwork
                |> expectTxError "refund tx"

              do! broadcaster.BroadcastTx(refundTx, quoteAsset)
              return
                [RefundTxPublished(refundTx.GetHash()); ]
                |> enhance

              | _ -> return []
            }
          return events @ maybeSwapTxConfirmedEvent @ maybeSpendTxEvent @ publishEvent
        | _, Finished _ ->
          return []
        | x, s ->
          return raise <| Exception($"Unexpected Command \n{x} \n\nWhile in the state\n{s}")
      with
      | ex ->
        return! UnExpectedError ex |> Error
    }

  let private updateCost (state: State) (event: Event) (cost: SwapCost) =
    match event, state with
    // loop out
    | PrePayFinished { RoutingFee = fee; AmountPayed = amount }, Out(_, x) ->
      { cost with
          OffChain = x.Cost.OffChain + fee.ToMoney()
          ServerOffChain = x.Cost.ServerOffChain + amount.ToMoney() }
    | OffchainOfferResolved { RoutingFee = fee; AmountPayed = amount }, Out(_, x) ->
      { cost with
          OffChain = x.Cost.OffChain + fee.ToMoney()
          ServerOffChain = x.Cost.ServerOffChain + amount.ToMoney() }
    | SweepTxConfirmed(_, sweepTxAmount), Out(_, x) ->
      let swapTxAmount = x.OnChainAmount
      { cost
          with
          ServerOnChain = x.Cost.ServerOnChain - sweepTxAmount
          OnChain = sweepTxAmount - swapTxAmount
          }

    // loop in
    | OurSwapTxPublished(fee, _), In(_, x) ->
      { x.Cost with OnChain = fee }
    | SuccessTxConfirmed (htlcAmount, _txid), In(_, x) ->
      { x.Cost with ServerOnChain = x.Cost.ServerOnChain + htlcAmount }
    | RefundTxConfirmed (fee, _txid), In(_, x) ->
      { x.Cost with OnChain = x.Cost.OnChain + fee }
    | OffChainPaymentReceived amt, In(_, x) ->
      { x.Cost with ServerOffChain = x.Cost.ServerOffChain - amt }
    | x -> failwith $"Unreachable! %A{x}"

  let applyChanges
    (state: State) (event: Event) =
    match event, state with
    | NewLoopOutAdded(h, x), HasNotStarted ->
      Out (h, x)
    | ClaimTxPublished txid, Out(h, x) ->
      Out (h, { x with ClaimTransactionId = Some txid })
    | TheirSwapTxPublished tx, Out(h, x) ->
      Out (h, { x with LockupTransactionHex = Some tx })
    | PrePayFinished _, Out(h, x) ->
      Out(h, { x with
                 Cost = updateCost state event x.Cost })
    | OffchainOfferResolved _, Out(h, x) ->
      Out(h, { x with
                 IsOffchainOfferResolved = true
                 Cost = updateCost state event x.Cost })
    | SweepTxConfirmed(txid, _), Out(h, x) ->
      let cost = updateCost state event x.Cost
      Out(h, { x with IsClaimTxConfirmed = true; ClaimTransactionId = Some txid; Cost = cost })
    | NewLoopInAdded(h, x), HasNotStarted ->
      In (h, x)
    | OurSwapTxPublished(fee, tx), In(h, x) ->
      let cost = { x.Cost with OnChain = fee }
      In (h, { x with LockupTransactionHex = Some tx; Cost = cost })
    | OurSwapTxConfirmed(txid, vOut), In(h, x) ->
      In(h, { x with LockupTransactionOutPoint = Some (txid, vOut) })
    | RefundTxPublished txid, In(h, x) ->
      In(h, { x with RefundTransactionId = Some txid })
    | SuccessTxConfirmed _, In(h, x) ->
      In(h, { x with Cost = updateCost state event x.Cost })
    | RefundTxConfirmed _, In(h, x) ->
      In(h, { x with Cost = updateCost state event x.Cost })
    | OffChainPaymentReceived _, In(h, x) ->
      In(h, { x with Cost = updateCost state event x.Cost })

    | FinishedByError (_, err), In(_, { Cost = cost }) ->
      Finished(cost, FinishedState.Errored(err))
    | FinishedByError (_, err), Out(_, { Cost = cost }) ->
      Finished(cost, FinishedState.Errored(err))
    | FinishedSuccessfully _, Out (_ ,{ Cost = cost })
    | FinishedSuccessfully _, In(_, { Cost = cost }) ->
      Finished(cost, FinishedState.Success)
    | FinishedByRefund _, In (_h, { Cost = cost; RefundTransactionId = Some txid }) ->
      Finished(cost, FinishedState.Refunded(txid))
    | FinishedByTimeout(_, reason), Out(_, { Cost = cost })
    | FinishedByTimeout(_, reason), In(_, { Cost = cost }) ->
      Finished(cost, FinishedState.Timeout(reason))
    | NewTipReceived h, Out(_, x) ->
      Out(h, x)
    | NewTipReceived h, In(_, x) ->
      In(h, x)
    | _, x -> x

  type Aggregate = Aggregate<State, Command, Event, Error, uint16 * DateTime>
  type Handler = Handler<State, Command, Event, Error, SwapId>

  let getAggregate deps: Aggregate = {
    Zero = State.Zero
    Exec = executeCommand deps
    Aggregate.Apply = applyChanges
    Filter = id
    Enrich = id
    SortBy = fun event ->
      event.Data.EventTag, event.Meta.EffectiveDate.Value
  }

  let getRepository eventStoreUri =
    let store = eventStore eventStoreUri
    Repository.Create
      store
      serializer
      entityType

  type EventWithId = {
    Id: SwapId
    Event: Event
  }

  type ErrorWithId = {
    Id: SwapId
    Error: EventSourcingError<Error>
  }

  let getHandler aggr eventStoreUri =
    getRepository eventStoreUri
    |> Handler.Create aggr

