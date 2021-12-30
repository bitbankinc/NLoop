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
/// Swap domain.
///
/// Below is the list of ubiquitous languages
/// * SwapTx (a.k.a. LockupTx) ... On-Chain TX which offers funds with HTLC.
/// * ClaimTx ... TX to take funds from SwapTx in exchange of preimage. a.k.a "sweep" (loop out)
/// * RefundTx ... TX to take funds from SwapTx in case of the timeout (loop in).
/// * SuccessTx ... counterparty's claim tx (loop in)
/// * SpendTx ... RefundTx & SuccessTx
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

  [<Struct>]
  type Group = {
    Category: Category
    PairId: PairId
  }
    with
    member this.OffChainAsset =
      match this.Category with
      | Out ->
        let struct(_b, q) = this.PairId.Value
        q
      | In ->
        let struct (b, _q) = this.PairId.Value
        b
    member this.OnChainAsset =
      match this.Category with
      | Out ->
        let struct(b, _q) = this.PairId.Value
        b
      | In ->
        let struct (_b, q) = this.PairId.Value
        q

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
  [<Literal>]
  let entityType = "swap"

  module Constants =
    let MinPreimageRevealDelta = BlockHeightOffset32(20u)

    /// Used in loop-out.
    /// Default confirmation target we will use when sweeping the funds.
    /// This will be used if we reach too close to the expiration height, thus we must hurry the confirmation.
    let DefaultSweepConfTarget = BlockHeightOffset32 9u

    /// Used in loop-out.
    /// If the number of remaining blocks until the timeout gets smaller than this value, we start using smaller
    /// conf target for estimating the fee for a sweep tx.
    let DefaultSweepConfTargetDelta = BlockHeightOffset32 18u

  open Constants

  type LoopOutParams = {
    MaxPrepayFee: Money
    MaxPaymentFee: Money
    Height: BlockHeight
  }

  type PayInvoiceResult = {
    RoutingFee: LNMoney
    AmountPayed: LNMoney
  }

  type Command =
    // -- loop out --
    /// Start new loop out (i.e. reverse-submarine swap)
    | NewLoopOut of LoopOutParams * LoopOut
    /// We cannot tell which tx is swap tx unless the counterparty tell us about it,
    /// When they did, use this command.
    | CommitSwapTxInfoFromCounterParty of swapTxHex: string
    /// Tell the domain that they received our payment offer.
    | OffChainOfferResolve of PayInvoiceResult

    // -- loop in --
    /// Start new loop in.
    | NewLoopIn of height: BlockHeight * LoopIn
    /// Tell the domain that we received their payment offer.
    | CommitReceivedOffChainPayment of amt: Money

    // -- both
    /// Additional way to mark this swap as errored
    | MarkAsErrored of err: string
    /// Feed the every block you get from the blockchain with this command.
    /// We will scan it to see if there is something we are interested in it.
    | NewBlock of block: BlockWithHeight * cryptoCode: SupportedCryptoCode
    /// Tell the domain about the chain reorg with this command.
    /// past on-chain events which took place on the block will be skipped.
    | UnConfirmBlock of blockHash: uint256

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
    | ClaimTxConfirmed of blockHash: uint256 * txid: uint256 * sweepAmount: Money
    | PrePayFinished of PayInvoiceResult
    | OffchainOfferResolved of PayInvoiceResult
    | TheirSwapTxPublished of txHex: string
    | TheirSwapTxConfirmedFirstTime of {| BlockHash: uint256; Height: BlockHeight |}

    // -- loop in --
    | NewLoopInAdded of height: BlockHeight * loopIn: LoopIn
    // We do not use `Transaction` type just to make serializer happy.
    // if we stop using json serializer for serializing this event, we can use `Transaction` instead.
    // But it seems that just using string is much simpler.
    | OurSwapTxPublished of fee: Money * txHex: string * htlcOutIndex: uint
    | OurSwapTxConfirmed of blockHash: uint256 * txid: uint256 * htlcOutIndex: uint
    | RefundTxPublished of txid: uint256
    | RefundTxConfirmed of blockHash: uint256 * fee: Money * txid: uint256
    | SuccessTxConfirmed of blockHash: uint256 * htlcValue: Money * txid: uint256
    | OffChainPaymentReceived of amt: Money

    // -- general --
    | NewTipReceived of blockHash: uint256 * BlockHeight
    | BlockUnConfirmed of blockHash: uint256

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
      | ClaimTxConfirmed _ -> 4us
      | PrePayFinished _ -> 5us
      | TheirSwapTxPublished _ -> 6us
      | OffChainPaymentReceived _ -> 7us
      | TheirSwapTxConfirmedFirstTime _ -> 8us

      | NewLoopInAdded _ -> 256us + 0us
      | OurSwapTxPublished _ -> 256us + 1us
      | OurSwapTxConfirmed _ -> 256us + 2us
      | RefundTxPublished _-> 256us + 3us
      | RefundTxConfirmed _ -> 256us + 4us
      | SuccessTxConfirmed _ -> 256us + 5us

      | NewTipReceived _ -> 512us + 0us
      | BlockUnConfirmed _ -> 512us + 1us

      | FinishedSuccessfully _ -> 1024us + 0us
      | FinishedByRefund _ -> 1024us + 1us
      | FinishedByError _ -> 1024us + 2us
      | FinishedByTimeout _ -> 1024us + 3us

      | UnknownTagEvent (t, _) -> t

    /// returns the block hash for which the on-chain event took place.
    /// If an event is not on-chain, returns None.
    member this.WhichBlock =
      match this with
      | ClaimTxConfirmed(blockHash, _, _) -> Some blockHash
      | TheirSwapTxConfirmedFirstTime item -> Some item.BlockHash
      | OurSwapTxConfirmed(blockHash, _, _) -> Some blockHash
      | RefundTxConfirmed(blockHash, _, _) -> Some blockHash
      | SuccessTxConfirmed(blockHash, _, _)-> Some blockHash
      | NewTipReceived(blockHash, _) -> Some blockHash
      | _ -> None

    member this.IsTerminal =
      match this with
      | FinishedSuccessfully _ -> true
      | FinishedByRefund _ -> true
      | FinishedByError _ -> true
      | FinishedByTimeout _ -> true
      | _ -> false

    member this.IsOnChainEvent =
      this.WhichBlock.IsSome

    member this.Type =
      match this with
      | NewLoopOutAdded _ -> "new_loop_out_added"
      | ClaimTxPublished _ -> "claim_tx_published"
      | OffChainOfferStarted _ -> "offchain_offer_started"
      | OffchainOfferResolved _ -> "offchain_offer_resolved"
      | ClaimTxConfirmed _ -> "sweep_tx_confirmed"
      | PrePayFinished _ -> "prepay_finished"
      | TheirSwapTxPublished _ -> "their_swap_tx_published"
      | OffChainPaymentReceived _ -> "offchain_payment_received"
      | TheirSwapTxConfirmedFirstTime _ -> "their_swap_tx_confirmed_first_time"

      | NewLoopInAdded _ -> "new_loop_in_added"
      | OurSwapTxPublished _ -> "our_swap_tx_published"
      | OurSwapTxConfirmed _ -> "our_swap_tx_confirmed"
      | RefundTxPublished _ -> "refund_tx_published"
      | RefundTxConfirmed _ -> "refund_tx_confirmed"
      | SuccessTxConfirmed _ -> "success_tx_confirmed"

      | NewTipReceived _ -> "new_tip_received"
      | BlockUnConfirmed _ -> "block_unconfirmed"

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
    | APIMisuseError of string
    with
    member this.Msg =
      "SwapError: " +
      match this with
      | UTXOProviderError e -> e.Msg
      | x -> x.ToString()

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
            | 0us | 1us | 2us | 3us | 4us | 5us | 6us | 7us | 8us
            | 256us | 257us | 258us | 259us | 260us | 261us
            | 512us | 513us
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
    (swapTx: Transaction)
    (loopOut: LoopOut): Task<Result<_ option, Error>> = taskResult {
      let struct (baseAsset, _quoteAsset) = loopOut.PairId.Value
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
          swapTx
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
        let checkHeight newBlockHash (height: BlockHeight) (oldHeight: BlockHeight) =
          let one = BlockHeightOffset16.One
          if height <= oldHeight + one then
            [NewTipReceived(newBlockHash, height)] |> enhance |> Ok
          elif oldHeight + one < height then
            assert false
            $"Bogus block height. The block has been skipped. This should never happen."
            |> APIMisuseError |> Error
          else
            failwith "unreachable"

        match cmd.Data, s with
        // --- loop out ---
        | NewLoopOut({ Height = h } as p, loopOut), HasNotStarted ->
          do! loopOut.Validate() |> expectInputError
          let! additionalEvents =
            if loopOut.PrepayInvoice |> String.IsNullOrEmpty |> not then
              let prepaymentParams =
                { PayInvoiceParams.MaxFee =  p.MaxPrepayFee
                  OutgoingChannelIds = loopOut.OutgoingChanIds }
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
                OutgoingChannelIds = loopOut.OutgoingChanIds
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
        | CommitSwapTxInfoFromCounterParty swapTxHex, Out(height, loopOut) ->
          let e =
            swapTxHex |> TheirSwapTxPublished

          if loopOut.AcceptZeroConf then
            let tx = Transaction.Parse(swapTxHex, loopOut.QuoteAssetNetwork)
            match! sweepOrBump deps height tx loopOut with
            | Some claimTxId ->
              return [e; ClaimTxPublished(claimTxId)] |> enhance
            | None -> return [e] |> enhance
          else
            return [e] |> enhance

        // --- loop in ---
        | NewLoopIn(h, loopIn), HasNotStarted ->
          do! loopIn.Validate() |> expectInputError
          let! additionalEvents = taskResult {
              let struct (_baseAsset, quoteAsset) = loopIn.PairId.Value
              let! utxos =
                utxoProvider.GetUTXOs(loopIn.ExpectedAmount, quoteAsset)
                |> TaskResult.mapError(UTXOProviderError)
              let! feeRate =
                feeEstimator.Estimate
                  loopIn.HTLCConfTarget
                  quoteAsset
              let! change =
                getChangeAddress.Invoke(quoteAsset)
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
              let! psbt = utxoProvider.SignSwapTxPSBT(psbt, quoteAsset)
              match psbt.TryFinalize() with
              | false, e ->
                return raise <| Exception $"%A{e |> Seq.toList}"
              | true, _ ->
                let tx = psbt.ExtractTransaction()
                let fee = feeRate.GetFee(tx)
                if loopIn.MaxMinerFee < fee then
                  // we give up executing the swap here rather than dealing with the fee-market nitty-gritty.
                  return [
                    let msg = $"OnChain FeeRate is too high. (actual fee: {fee}. Our maximum: {loopIn.MaxMinerFee})"
                    FinishedByError(loopIn.Id, msg)
                  ]
                else
                  do! broadcaster.BroadcastTx(tx, quoteAsset)
                  let! index =
                    tx.ValidateOurSwapTxOut(loopIn.RedeemScript, loopIn.ExpectedAmount)
                    |> expectBogusSwapTx
                  return [
                    OurSwapTxPublished(fee, tx.ToHex(), index)
                  ]
          }
          return [NewLoopInAdded(h, loopIn)] @ additionalEvents |> enhance
        | CommitReceivedOffChainPayment amt, In(_, loopIn) ->
          return [
            OffChainPaymentReceived(amt)
            if loopIn.IsOurSuccessTxConfirmed then
              FinishedSuccessfully(loopIn.Id)
          ] |> enhance
        // --- ---

        | MarkAsErrored(err), Out(_, { Id = swapId })
        | MarkAsErrored(err), In (_ , { Id = swapId }) ->
          return [FinishedByError( swapId, err )] |> enhance
        | NewBlock ({ Height = height; Block = block }, cc), Out(oldHeight, ({ ClaimTransactionId = maybePrevClaimTxId; PairId = PairId(struct(baseAsset, _)); TimeoutBlockHeight = timeout } as loopOut))
          when baseAsset = cc ->
            let! events = (height, oldHeight) ||> checkHeight (block.Header.GetHash())

            // To make it reorg-safe, we must track the confirmation of swap tx.
            let maybeSwapTxConfirmedEvent =
              let maybeSwapTx =
                loopOut.SwapTx
                |> Option.bind(fun swapTx -> block.Transactions |> Seq.tryFind(fun t -> t.GetHash() = swapTx.GetHash()))
              match maybeSwapTx with
              | Some _ when loopOut.SwapTxHeight.IsNone ->
                [TheirSwapTxConfirmedFirstTime({| Height = height; BlockHash = block.Header.GetHash() |})] |> enhance
              | _ ->
                []

            let events = events @  maybeSwapTxConfirmedEvent

            let maybeClaimTx =
              maybePrevClaimTxId
              |> Option.bind(fun txid -> block.Transactions |> Seq.tryFind(fun t -> t.GetHash() = txid))
            match maybeClaimTx with
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
                ClaimTxConfirmed(block.Header.GetHash(), sweepTx.GetHash(), sweepTxAmount)
                // We do not mark it as finished until the counterparty receives their share.
                // This is because we don't know how much we have payed as off-chain routing fee until counterparty
                // receives. (Usually they receive it way before the sweep tx is confirmed so it is very rare case that
                // this will be false.)
                if loopOut.IsOffchainOfferResolved then
                  FinishedSuccessfully(loopOut.Id)
              ]
              return events @ (additionalEvents |> enhance)
            | None ->
            // we may have to create or bump the sweep tx.
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
                match loopOut.SwapTx with
                | Some tx when loopOut.IsSwapTxConfirmedEnough(height) && not <| loopOut.IsClaimTxConfirmed ->
                  match! sweepOrBump deps height tx loopOut with
                  | Some txid ->
                    return [ClaimTxPublished(txid);] |> enhance
                  | None ->
                    return []
                | Some _ ->
                  // we have to wait for more confirmations before publishing the ClaimTx.
                  return []
                | None ->
                  // When we don't know the swap tx, there is no way we can claim our share.
                  // we have to wait until the counterparty tells us about it.
                  return []
              }
            return events @ additionalEvents

        | NewBlock ({ Height = height; Block = block }, cc), In(oldHeight, loopIn) when loopIn.PairId.Quote = cc ->
          let! events = (height, oldHeight) ||> checkHeight (block.Header.GetHash())
          let! e =
            taskResult {
              // iff we have already published our swap tx, then we are going to do 3 checks here.
              // 1. check if our swap tx is confirmed
              // 2. check the spend tx (i.e. the tx spending from the swap tx.) is confirmed
              // 3. if we reached a timeout or not.
              match loopIn.SwapTxInfo with
              | Some(swapTx, vOut) ->
                let! swapTxConfirmationEvents = taskResult {
                  match block.Transactions |> Seq.tryFind(fun tx -> tx.GetHash() = swapTx.GetHash()) with
                  | Some tx ->
                    // 1. our swap tx is confirmed.
                    let! index =
                      tx.ValidateOurSwapTxOut(loopIn.RedeemScript, loopIn.ExpectedAmount)
                      |> expectBogusSwapTx
                    return
                      [
                        OurSwapTxConfirmed(block.Header.GetHash(), tx.GetHash(), index)
                      ]
                   | _ -> return []
                }
                let maybeSpendTx =
                  let pickSpendTx (tx: Transaction) =
                    let txInPicker (i: TxIn) =
                      if i.PrevOut.Hash = swapTx.GetHash() && i.PrevOut.N = vOut then
                        Some(tx, i)
                      else
                        None
                    tx.Inputs
                    |> Seq.tryPick txInPicker
                  block.Transactions
                  |> Seq.tryPick pickSpendTx
                let spendTxConfirmationEvents =
                  match maybeSpendTx with
                  | Some(spendTx, spendTxIn) ->
                    // 2. spend tx is confirmed.
                    if spendTxIn.WitScript |> Scripts.isSuccessWitness then
                      // the spend tx is their claim tx.
                      [
                        let htlcOutValue = swapTx.Outputs.[vOut].Value
                        SuccessTxConfirmed(block.Header.GetHash(), htlcOutValue, spendTx.GetHash())
                        if loopIn.IsOffChainPaymentReceived then
                          FinishedSuccessfully(loopIn.Id)
                      ]
                    else
                      // the spend tx is our refund tx.
                      let refundTxFee =
                        swapTx.Outputs.AsCoins()
                        |> Seq.cast
                        |> Seq.toArray
                        |> spendTx.GetFee
                      [
                        RefundTxConfirmed(block.Header.GetHash(), refundTxFee, spendTx.GetHash())
                        FinishedByRefund loopIn.Id
                      ]
                  | None -> []

                let! refundEvents = taskResult {
                  if maybeSpendTx.IsNone && loopIn.TimeoutBlockHeight <= height then
                    let q = loopIn.PairId.Quote
                    let! addr =
                      getRefundAddress.Invoke(q)
                      |> TaskResult.mapError FailedToGetAddress
                    let! fee = feeEstimator.Estimate loopIn.HTLCConfTarget q
                    let! refundTx =
                      Transactions.createRefundTx
                        (swapTx.ToHex())
                        loopIn.RedeemScript
                        fee
                        addr
                        loopIn.RefundPrivateKey
                        loopIn.TimeoutBlockHeight
                        loopIn.QuoteAssetNetwork
                      |> expectTxError "refund tx"
                    do! broadcaster.BroadcastTx(refundTx, q)
                    return
                      [RefundTxPublished(refundTx.GetHash())]
                  else
                    return []
                }
                return swapTxConfirmationEvents @ spendTxConfirmationEvents @ refundEvents
              | _ ->
                return []
            }
          return events @ (e |> enhance)
        | UnConfirmBlock(blockHash), Out(_heightBefore, _)
        | UnConfirmBlock(blockHash), In (_heightBefore, _) ->
            return [BlockUnConfirmed(blockHash)] |> enhance
        | _, Finished _ ->
          return []
        | x, s ->
#if DEBUG
          return failwith $"Unexpected Command \n{x} \n\nWhile in the state\n{s}"
#else
          return []
#endif
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
    | ClaimTxConfirmed(_, _, sweepTxAmount), Out(_, x) ->
      let swapTxAmount = x.OnChainAmount
      { cost
          with
          ServerOnChain = x.Cost.ServerOnChain - sweepTxAmount
          OnChain = sweepTxAmount - swapTxAmount
          }

    // loop in
    | OurSwapTxPublished(fee, _, _), In(_, x) ->
      { x.Cost with OnChain = fee }
    | SuccessTxConfirmed (_, htlcAmount, _txid), In(_, x) ->
      { x.Cost with ServerOnChain = x.Cost.ServerOnChain + htlcAmount }
    | RefundTxConfirmed (_blockHash, fee, _txid), In(_, x) ->
      { x.Cost with OnChain = x.Cost.OnChain + fee }
    | OffChainPaymentReceived amt, In(_, x) ->
      { x.Cost with ServerOffChain = x.Cost.ServerOffChain - amt; }
    | x -> failwith $"Unreachable! %A{x}"

  let applyChanges
    (state: State) (event: Event) =
    match event, state with
    | NewLoopOutAdded(h, x), HasNotStarted ->
      Out (h, x)
    | ClaimTxPublished txid, Out(h, x) ->
      Out (h, { x with ClaimTransactionId = Some txid })
    | TheirSwapTxPublished tx, Out(h, x) ->
      Out (h, { x with SwapTxHex = Some tx })
    | TheirSwapTxConfirmedFirstTime item, Out(h, x) ->
      Out(h, { x with SwapTxHeight = Some item.Height })
    | PrePayFinished _, Out(h, x) ->
      Out(h, { x with
                 Cost = updateCost state event x.Cost })
    | OffchainOfferResolved _, Out(h, x) ->
      Out(h, { x with
                 Cost = updateCost state event x.Cost })
    | ClaimTxConfirmed(_, txid, _), Out(h, x) ->
      let cost = updateCost state event x.Cost
      Out(h, { x with IsClaimTxConfirmed = true; ClaimTransactionId = Some txid; Cost = cost })
    | NewLoopInAdded(h, x), HasNotStarted ->
      In (h, x)
    | OurSwapTxPublished(fee, txHex, vOut), In(h, x) ->
      let cost = { x.Cost with OnChain = fee }
      In (h, { x with SwapTxInfoHex = Some { TxHex = txHex; N = vOut }; Cost = cost })
    | RefundTxPublished txid, In(h, x) ->
      In(h, { x with RefundTransactionId = Some txid })
    | SuccessTxConfirmed _, In(h, x) ->
      In(h, { x with Cost = updateCost state event x.Cost; IsOurSuccessTxConfirmed = true })
    | RefundTxConfirmed _, In(h, x) ->
      In(h, { x with Cost = updateCost state event x.Cost })
    | OffChainPaymentReceived _, In(h, x) ->
      In(h, { x with Cost = updateCost state event x.Cost; IsOffChainPaymentReceived = true })

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
    | NewTipReceived(_blockHash, h), Out(_, x) ->
      Out(h, x)
    | NewTipReceived(_blockHash, h), In(_, x) ->
      In(h, x)
    | _, x -> x

  type Aggregate = Aggregate<State, Command, Event, Error, uint16 * DateTime>
  type Handler = Handler<State, Command, Event, Error, SwapId>

  let getAggregate deps: Aggregate = {
    Zero = State.Zero
    Exec = executeCommand deps
    Aggregate.Apply = applyChanges
    Filter =
      fun recordedEvents ->
        let unconfirmedBlockHashes =
          recordedEvents
          |> List.choose(
            fun re ->
              match re.Data with
              | Event.BlockUnConfirmed(blockHash) -> Some blockHash
              | _ -> None)
        // skip all on-chain events for unconfirmed blocks.
        recordedEvents
        |> List.filter(fun re ->
          match re.Data.WhichBlock with
          | Some blockHash when unconfirmedBlockHashes |> List.contains blockHash -> false
          | _ -> true
          )
    Enrich = id
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

