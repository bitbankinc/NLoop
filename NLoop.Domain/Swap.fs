namespace NLoop.Domain

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open System.Threading.Tasks
open DotNetLightning.Utils
open FSharp.Control.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils.Primitives
open NBitcoin
open NBitcoin.RPC
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
    | Finished of finalCost: SwapCost * FinishedState * group: Group
    with
    static member Zero = HasNotStarted

    member this.Cost =
      match this with
      | HasNotStarted _ -> SwapCost.Zero
      | Out(_, { Cost = cost })
      | In(_, { Cost = cost })
      | Finished(cost, _, _) -> cost

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

  type OfferResult = unit

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

  type NewLoopOutAddedData = {
    Height: BlockHeight
    LoopOut: LoopOut
  }

  type OffChainOfferStartedData = {
    SwapId: SwapId
    PairId: PairId
    InvoiceStr: string
    OfferResult: OfferResult
  }
    with
    member this.Invoice = this.InvoiceStr |> PaymentRequest.Parse

  type ClaimTxPublishedData = {
    Txid: uint256
  }
  type ClaimTxConfirmedData = {
    BlockHash: uint256
    TxId: uint256
    SweepAmount: Money
  }

  type PrepayFinishedData = {
    Result: PayInvoiceResult
  }
  type OffchainOfferResolvedData = {
    Result: PayInvoiceResult
  }
  type TheirSwapTxPublishedData = {
    TxHex: string
  }

  type TheirSwapTxConfirmedFirstTimeData = {
    BlockHash: uint256
    TxHex: string option
    Height: BlockHeight
  }

  type NewLoopInAddedData = {
    Height: BlockHeight
    LoopIn: LoopIn
  }

  type OurSwapTxPublishedData = {
    Fee: Money
    TxHex: string
    HtlcOutIndex: uint
  }
  type OurSwapTxConfirmedData = {
    BlockHash: uint256
    TxId: uint256
    HTlcOutIndex: uint
  }

  type RefundTxPublishedData = {
    TxId: uint256
  }
  type RefundTxConfirmedData = {
    BlockHash: uint256
    Fee: Money
    Txid: uint256
  }

  type SuccessTxConfirmedData = {
    BlockHash: uint256
    HTLCValue: Money
    Txid: uint256
  }

  type OffChainPaymentReceivedData = {
    Amount: Money
  }
  type NewTipReceivedData = {
    BlockHash: uint256
    Height: BlockHeight
  }
  type BlockUnConfirmedData = {
    BlockHash: uint256
  }
  type FinishedByErrorData = {
    Id: SwapId
    Error: string
  }
  type FinishedSuccessfullyData = {
    Id: SwapId
  }
  type FinishedByRefundData = {
    Id: SwapId
  }
  type FinishedByTimeoutData = {
    Id: SwapId
    Reason: string
  }
  type UnknownTagEventData = {
    Tag: uint16
    Data: byte[]
  }

  let [<Literal>] new_loop_out_added = "new_loop_out_added"
  let [<Literal>] claim_tx_published = "claim_tx_published"
  let [<Literal>] offchain_offer_started = "offchain_offer_started"
  let [<Literal>] offchain_offer_resolved = "offchain_offer_resolved"
  let [<Literal>] claim_tx_confirmed =  "claim_tx_confirmed"
  let [<Literal>] prepay_finished =  "prepay_finished"
  let [<Literal>] their_swap_tx_published = "their_swap_tx_published"
  let [<Literal>] offchain_payment_received = "offchain_payment_received"
  let [<Literal>] their_swap_tx_confirmed_first_time = "their_swap_tx_confirmed_first_time"

  let [<Literal>] new_loop_in_added =  "new_loop_in_added"
  let [<Literal>] our_swap_tx_published =    "our_swap_tx_published"
  let [<Literal>] our_swap_tx_confirmed  =  "our_swap_tx_confirmed"
  let [<Literal>] refund_tx_published    =  "refund_tx_published"
  let [<Literal>] refund_tx_confirmed   =   "refund_tx_confirmed"
  let [<Literal>] success_tx_confirmed  =   "success_tx_confirmed"

  let [<Literal>] new_tip_received      =   "new_tip_received"
  let [<Literal>] block_unconfirmed     =   "block_unconfirmed"

  let [<Literal>] finished_successfully =   "finished_successfully"
  let [<Literal>] finished_by_refund    =   "finished_by_refund"
  let [<Literal>] finished_by_error     =   "finished_by_error"
  let [<Literal>] finished_by_timeout   =   "finished_by_timeout"
  let [<Literal>] unknown_tag_event =    "unknown_tag_event"

  /// in Event-Sourcing system, there is no DB migration.
  /// So events must be forward-compatible, i.e., old version must be able to deserialize the newer events.
  /// We use json serializer and record types to achieve this goal. json serializer
  /// will ignore the unknown field in the record when deserializing. and if the union case itself is unknown,
  /// it will deserialize it as `UnknownTagEvent` and do not use for state-reconstruction.
  /// So the rule of thumb is
  /// 0. The union must always hold record types as its data.
  /// 1. You can add member to the field freely, as long as its member has type `option` or `ValueOption`
  /// 2. You cannot remove (or alter) the member from the field, you must create another union-case and treat it as a
  ///    new type of an event in that case.
  /// 3. If you add a new union-case, define the unique (and previously unused) two-bytes tag for it and add it to
  ///    KnownTags.
  ///
  /// As a result, this is a pretty much the same versioning strategy with Protobuf.
  type Event =
    // -- loop out --
    | NewLoopOutAdded of NewLoopOutAddedData
    | OffChainOfferStarted of OffChainOfferStartedData
    | ClaimTxPublished of ClaimTxPublishedData
    | ClaimTxConfirmed of ClaimTxConfirmedData
    | PrePayFinished of PrepayFinishedData
    | OffchainOfferResolved of OffchainOfferResolvedData
    | TheirSwapTxPublished of TheirSwapTxPublishedData
    | TheirSwapTxConfirmedFirstTime of TheirSwapTxConfirmedFirstTimeData

    // -- loop in --
    | NewLoopInAdded of NewLoopInAddedData
    | OurSwapTxPublished of OurSwapTxPublishedData
    | OurSwapTxConfirmed of OurSwapTxConfirmedData
    | RefundTxPublished of RefundTxPublishedData
    | RefundTxConfirmed of RefundTxConfirmedData
    | SuccessTxConfirmed of SuccessTxConfirmedData
    | OffChainPaymentReceived of OffChainPaymentReceivedData

    // -- general --
    | NewTipReceived of NewTipReceivedData
    | BlockUnConfirmed of BlockUnConfirmedData

    | FinishedByError of FinishedByErrorData
    | FinishedSuccessfully of FinishedSuccessfullyData
    | FinishedByRefund of FinishedByRefundData
    | FinishedByTimeout of FinishedByTimeoutData
    | UnknownTagEvent of UnknownTagEventData
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

      | UnknownTagEvent { Tag = t } -> t

    static member KnownTags = [|
      for i in 0us..8us do
        i
      for i in 0us..5us do
        256us + i
      for i in 0us..1us do
        512us + i
      for i in 0us..3us do
        1024us + i
    |]
    /// returns the block hash for which the on-chain event took place.
    /// If an event is not on-chain, returns None.
    member this.WhichBlock =
      match this with
      | ClaimTxConfirmed { BlockHash = blockHash}
      | TheirSwapTxConfirmedFirstTime { BlockHash = blockHash }
      | OurSwapTxConfirmed { BlockHash = blockHash }
      | RefundTxConfirmed { BlockHash = blockHash }
      | SuccessTxConfirmed { BlockHash = blockHash }
      | NewTipReceived { BlockHash = blockHash } -> Some blockHash
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
      | NewLoopOutAdded _ -> new_loop_out_added
      | ClaimTxPublished _ -> claim_tx_published
      | OffChainOfferStarted _ -> offchain_offer_started
      | OffchainOfferResolved _ -> offchain_offer_resolved
      | ClaimTxConfirmed _ -> claim_tx_confirmed
      | PrePayFinished _ -> prepay_finished
      | TheirSwapTxPublished _ -> their_swap_tx_published
      | OffChainPaymentReceived _ -> offchain_payment_received
      | TheirSwapTxConfirmedFirstTime _ -> their_swap_tx_confirmed_first_time

      | NewLoopInAdded _ -> new_loop_in_added
      | OurSwapTxPublished _ -> our_swap_tx_published
      | OurSwapTxConfirmed _ -> our_swap_tx_confirmed
      | RefundTxPublished _ -> refund_tx_published
      | RefundTxConfirmed _ -> refund_tx_confirmed
      | SuccessTxConfirmed _ -> success_tx_confirmed

      | NewTipReceived _ -> new_tip_received
      | BlockUnConfirmed _ -> block_unconfirmed

      | FinishedSuccessfully _ -> finished_successfully
      | FinishedByRefund _ -> finished_by_refund
      | FinishedByError _ -> finished_by_error
      | FinishedByTimeout _ -> finished_by_timeout

      | UnknownTagEvent _ -> unknown_tag_event
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
    | InputError of string
    | CanNotSafelyRevealPreimage
    | CounterPartyReturnedBogusResponse of BogusResponseError
    | BogusSwapTransaction of msg: string
    | APIMisuseError of string
    | OfferFailed of string
    with
    member this.Msg =
      "SwapError: " +
      match this with
      | x -> x.ToString()

  let inline private expectTxError (txName: string) (r: Result<_, Transactions.Error>) =
    r |> Result.mapError(fun e -> $"Error while creating {txName}: {e.Message}" |> TransactionError)

  let inline private expectBogusSwapTx(r: Result<_, string>) =
    r |> Result.mapError(fun msg -> $"SwapTx is Bogus! This should never happen: {msg}" |> BogusSwapTransaction)

  let inline private expectInputError(r: Result<_, string>) =
    r |> Result.mapError InputError

  let inline private expectOfferError(r: Result<_, string>) =
    r |> Result.mapError OfferFailed
  let private jsonConverterOpts =
    let o = JsonSerializerOptions()
    o.AddNLoopJsonConverters()
    o

  let serializer : Serializer<Event> = {
    Serializer.EventToBytes = fun (e: Event) ->
      let v = e.EventTag |> fun t -> Utils.ToBytes(t, false)
      let b =
        match e with
        | UnknownTagEvent { Data = b } ->
          b
        | e -> JsonSerializer.SerializeToUtf8Bytes(e, jsonConverterOpts)
      Array.concat (seq [v; b])
    BytesToEvents =
      fun b ->
        try
          let e =
            match Utils.ToUInt16(b.[0..1], false) with
            | v when Event.KnownTags |> Array.contains v ->
              JsonSerializer.Deserialize(ReadOnlySpan<byte>.op_Implicit b.[2..], jsonConverterOpts)
            | v ->
              UnknownTagEvent { Tag = v; Data = b.[2..]}
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
    PayToAddress: PayToAddress
    GetRefundAddress: GetAddress
    /// Used for pre-paying miner fee.
    /// This is not for paying an actual swap invoice, since we cannot expect it to get finished immediately.
    PayInvoiceImmediate: SupportedCryptoCode -> PayInvoiceParams -> PaymentRequest -> Task<PayInvoiceResult>

    /// Make an off-chain offer. Do not wait until it completes.
    Offer: SupportedCryptoCode ->  PayInvoiceParams -> PaymentRequest -> Task<Result<OfferResult, string>>
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
    ({ Broadcaster = broadcaster; FeeEstimator = feeEstimator; PayToAddress = payToAddress
       GetRefundAddress = getRefundAddress
       PayInvoiceImmediate = payInvoiceImmediate; Offer = offer;
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
            [NewTipReceived { BlockHash = newBlockHash; Height= height}] |> enhance |> Ok
          elif oldHeight + one < height then
            assert false
            $"Bogus block height. The block has been skipped. This should never happen. (oldHeight {oldHeight}) (newHeight {height}) "
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
              |> payInvoiceImmediate
                   loopOut.PairId.Quote
                   prepaymentParams
              |> Task.map(fun p -> { PrepayFinishedData.Result = p } |> PrePayFinished |> List.singleton |> Ok)
            else
              Task.FromResult(Ok [])
          let invoice =
            loopOut.Invoice
            |> PaymentRequest.Parse
            |> ResultUtils.Result.deref
          do! invoice.AmountValue |> function | Some _ -> Ok() | None -> Error(Error.InputError($"invoice has no amount specified"))
          let! result =
            let offerParams = {
              PayInvoiceParams.MaxFee = p.MaxPrepayFee
              OutgoingChannelIds = loopOut.OutgoingChanIds
            }
            offer loopOut.PairId.Quote offerParams invoice
            |> Task.map expectOfferError
          return
            [
              NewLoopOutAdded { Height = h; LoopOut = loopOut }
              yield! additionalEvents
              OffChainOfferStarted {
                SwapId = loopOut.Id
                PairId = loopOut.PairId
                InvoiceStr = invoice.ToString()
                OfferResult = result
              }
            ]
            |> enhance
        | OffChainOfferResolve payInvoiceResult, Out(_h, loopOut) ->
          return [
            OffchainOfferResolved { Result = payInvoiceResult }
            if loopOut.IsClaimTxConfirmed then
              FinishedSuccessfully { Id = loopOut.Id }
          ] |> enhance
        | CommitSwapTxInfoFromCounterParty swapTxHex, Out(height, loopOut) ->
          let e =
            { TheirSwapTxPublishedData.TxHex = swapTxHex }
            |> TheirSwapTxPublished

          if loopOut.AcceptZeroConf && loopOut.ClaimTransactionId.IsNone then
            let tx = Transaction.Parse(swapTxHex, loopOut.QuoteAssetNetwork)
            match! sweepOrBump deps height tx loopOut with
            | Some claimTxId ->
              return [e; ClaimTxPublished { Txid = claimTxId }] |> enhance
            | None -> return [e] |> enhance
          else
            return [e] |> enhance

        // --- loop in ---
        | NewLoopIn(h, loopIn), HasNotStarted ->
          do! loopIn.Validate() |> expectInputError
          let! additionalEvents = taskResult {
              let struct (_baseAsset, quoteAsset) = loopIn.PairId.Value
              let! feeRate =
                feeEstimator.Estimate
                  loopIn.HTLCConfTarget
                  quoteAsset
              let! tx =
                let req = {
                  WalletFundingRequest.Amount = loopIn.ExpectedAmount
                  CryptoCode = quoteAsset
                  DestAddress = loopIn.SwapAddress
                  TargetConf = loopIn.HTLCConfTarget
                }
                payToAddress(req)
              let fee = feeRate.GetFee(tx)
              let! index =
                tx.ValidateOurSwapTxOut(loopIn.AddressType, loopIn.RedeemScript, loopIn.ExpectedAmount)
                |> expectBogusSwapTx
              return [
                OurSwapTxPublished { Fee = fee; TxHex = tx.ToHex(); HtlcOutIndex = index }
              ]
          }
          return [NewLoopInAdded{ Height = h; LoopIn = loopIn }] @ additionalEvents |> enhance
        | CommitReceivedOffChainPayment amt, In(_, loopIn) ->
          return [
            OffChainPaymentReceived { Amount = amt }
            if loopIn.IsOurSuccessTxConfirmed then
              FinishedSuccessfully{ Id = loopIn.Id }
          ] |> enhance
        // --- ---

        | MarkAsErrored(err), Out(_, { Id = swapId })
        | MarkAsErrored(err), In (_ , { Id = swapId }) ->
          return [FinishedByError { Id = swapId; Error = err }] |> enhance
        | NewBlock ({ Height = height; Block = block }, cc), Out(oldHeight, ({ ClaimTransactionId = maybePrevClaimTxId; PairId = PairId(struct(baseAsset, _)); TimeoutBlockHeight = timeout } as loopOut))
          when baseAsset = cc ->
            let! events = (height, oldHeight) ||> checkHeight (block.Header.GetHash())

            // To make it reorg-safe, we must track the confirmation of swap tx.
            let maybeSwapTxConfirmedEvent =
              // if have seen the swap tx in the block before, there is no need to search again.
              if loopOut.SwapTxHeight.IsSome then [] else
              let maybeSwapTx =
                match loopOut.SwapTx with
                | Some swapTx ->
                  let h = swapTx.GetHash()
                  let isTxIdMatch = fun (t: Transaction) -> t.GetHash() = h
                  block.Transactions |> Seq.tryFind isTxIdMatch
                | None ->
                  let isAddressMatch =
                    let addresses = loopOut.PossibleLockupAddress
                    fun (t: Transaction) ->
                      t.Outputs
                      |> Seq.exists(fun o ->
                        let addr = o.ScriptPubKey.GetDestinationAddress(loopOut.BaseAssetNetwork)
                        addresses|> Seq.contains addr
                      )
                  block.Transactions |> Seq.tryFind isAddressMatch

              maybeSwapTx
              |> Option.map(fun tx ->
                TheirSwapTxConfirmedFirstTime({ Height = height; BlockHash = block.Header.GetHash(); TxHex = tx.ToHex() |> Some })
              )
              |> Option.toList
              |> enhance

            let events = events @  maybeSwapTxConfirmedEvent

            // first we check claim tx by id.
            let maybeClaimTx =
              maybePrevClaimTxId
              |> Option.bind(fun txid -> block.Transactions |> Seq.tryFind(fun t -> t.GetHash() = txid))

            // for rare case that after we bumped the claim tx, and old one gets confirmed, we must also check
            // tx inputs/outputs.
            let maybeClaimTx =
              match maybeClaimTx, loopOut.SwapTx with
              | Some x, _ -> Some x
              | None, Some swapTx ->
                let isFromSwapTx (tx: Transaction) =
                  tx.Inputs |> Seq.exists(fun txIn -> txIn.PrevOut.Hash = swapTx.GetHash())
                let isToClaimAddr (tx: Transaction) =
                  tx.Outputs |> Seq.exists(fun txOut -> txOut.ScriptPubKey.GetDestinationAddress(loopOut.BaseAssetNetwork).ToString() = loopOut.ClaimAddress)
                block.Transactions
                |> Seq.tryFind(fun tx -> isFromSwapTx tx && isToClaimAddr tx)
              | _ -> None
            match maybeClaimTx with
            | Some claimTx ->
              let claimTxAmount =
                let claimAddrPicker =
                  fun (o: TxOut) ->
                    if o.ScriptPubKey.GetDestinationAddress(loopOut.BaseAssetNetwork).ToString() = loopOut.ClaimAddress then
                      Some(o.Value)
                    else
                      None
                claimTx.Outputs
                |> Seq.pick(claimAddrPicker)
              // Our sweep tx is confirmed, this swap is finished!
              let additionalEvents = [
                ClaimTxConfirmed {
                  BlockHash = block.Header.GetHash()
                  TxId =  claimTx.GetHash()
                  SweepAmount = claimTxAmount
                }
                // We do not mark it as finished until the counterparty receives their share.
                // This is because we don't know how much we have payed as off-chain routing fee until counterparty
                // receives. (Usually they receive it way before the sweep tx is confirmed so it is very rare case that
                // this will be false.)
                if loopOut.IsOffchainOfferResolved then
                  FinishedSuccessfully { Id = loopOut.Id }
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
                return [FinishedByTimeout { Id = loopOut.Id; Reason = msg }] |> enhance
              else
                match loopOut.SwapTx with
                | Some tx when loopOut.IsSwapTxConfirmedEnough(height) && not <| loopOut.IsClaimTxConfirmed ->
                  match! sweepOrBump deps height tx loopOut with
                  | Some txid ->
                    return [ClaimTxPublished { Txid = txid }] |> enhance
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
                      tx.ValidateOurSwapTxOut(loopIn.AddressType, loopIn.RedeemScript, loopIn.ExpectedAmount)
                      |> expectBogusSwapTx
                    return
                      [
                        OurSwapTxConfirmed {
                          BlockHash = block.Header.GetHash()
                          TxId = tx.GetHash()
                          HTlcOutIndex =  index
                        }
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
                        SuccessTxConfirmed {
                          BlockHash =block.Header.GetHash()
                          Txid = spendTx.GetHash()
                          HTLCValue = htlcOutValue
                        }
                        if loopIn.IsOffChainPaymentReceived then
                          FinishedSuccessfully { Id = loopIn.Id }
                      ]
                    else
                      // the spend tx is our refund tx.
                      let refundTxFee =
                        swapTx.Outputs.AsCoins()
                        |> Seq.cast
                        |> Seq.toArray
                        |> spendTx.GetFee
                      [
                        RefundTxConfirmed {
                          BlockHash = block.Header.GetHash()
                          Fee = refundTxFee
                          Txid = spendTx.GetHash()
                        }
                        FinishedByRefund { Id = loopIn.Id }
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
                      [RefundTxPublished { TxId = refundTx.GetHash() }]
                  else
                    return []
                }
                return swapTxConfirmationEvents @ spendTxConfirmationEvents @ refundEvents
              | _ ->
                return []
            }
          return events @ (e |> enhance)
        | NewBlock _, HasNotStarted ->
          // ignore if the new block has come before we start anything
          return []
        | NewBlock _, Out _
        | NewBlock _, In _ ->
          // ignore if it is the cryptocode that we are not interested in.
          // If this case happens, it is safe but it hurts the performance.
          // Thus we check upper layer is using the API correctly only in debug build.
          assert false
          return []
        | UnConfirmBlock(blockHash), Out(_heightBefore, _)
        | UnConfirmBlock(blockHash), In (_heightBefore, _) ->
            return [BlockUnConfirmed { BlockHash = blockHash }] |> enhance
        | _, Finished _ ->
          return []
        | x, s ->
          return failwith $"Unexpected Command \n{x} \n\nWhile in the state\n{s}"
      with
      | ex ->
        return! UnExpectedError ex |> Error
    }

  let updateCost (state: State) (event: Event) (cost: SwapCost) =
    match event, state with
    // loop out
    | PrePayFinished { Result = { RoutingFee = fee; AmountPayed = amount } }, Out _ ->
      { cost
          with
          OffchainPrepayment = - amount.ToMoney()
          OffchainPrepaymentFee = - fee.ToMoney()
         }
    | OffchainOfferResolved { Result = { RoutingFee = fee; AmountPayed = amount } }, Out _ ->
      { cost with
          OffchainFee = - fee.ToMoney()
          OffchainPayment = - amount.ToMoney() }
    | ClaimTxConfirmed { SweepAmount = sweepTxAmount }, Out(_, x) ->
      let swapTxAmount = x.OnChainAmount
      { cost
          with
          OnchainPayment = sweepTxAmount
          OnchainFee = - (swapTxAmount - sweepTxAmount)
          }
    // loop in
    | OurSwapTxPublished { Fee = fee }, In(_, x) ->
      let swapTx, htlcOutIndex = x.SwapTxInfo.Value
      { cost with
          OnchainFee = - fee
          OnchainPayment = - swapTx.Outputs.[htlcOutIndex].Value  }
    | RefundTxConfirmed { Fee =  fee }, In(_, x) ->
      let refundValue = - x.Cost.OnchainPayment - fee
      { cost with
          OnchainFee = x.Cost.OnchainFee - fee
          OnchainPayment = refundValue  }
    | OffChainPaymentReceived { Amount = amt }, In _ ->
      { cost with OffchainPayment = amt; }
    | x -> failwith $"Unreachable! %A{x}"

  let applyChanges
    (state: State) (event: Event) =
    match event, state with
    | NewLoopOutAdded { Height = h; LoopOut = x }, HasNotStarted ->
      Out (h, x)
    | ClaimTxPublished { Txid = txid }, Out(h, x) ->
      Out (h, { x with ClaimTransactionId = Some txid })
    | TheirSwapTxPublished { TxHex = tx }, Out(h, x) ->
      Out (h, { x with SwapTxHex = Some tx })
    | TheirSwapTxConfirmedFirstTime item, Out(h, x) ->
      let swapTxHex =
        match item.TxHex with
        | Some x -> Some x
        | None -> x.SwapTxHex
      Out(h, { x with SwapTxHeight = Some item.Height; SwapTxHex = swapTxHex })
    | PrePayFinished _, Out(h, x) ->
      Out(h, { x with
                 Cost = updateCost state event x.Cost })
    | OffchainOfferResolved _, Out(h, x) ->
      Out(h, { x with
                 IsOffchainOfferResolved = true
                 Cost = updateCost state event x.Cost })
    | ClaimTxConfirmed { TxId = txid }, Out(h, x) ->
      let cost = updateCost state event x.Cost
      Out(h, { x with IsClaimTxConfirmed = true; ClaimTransactionId = Some txid; Cost = cost })
    | NewLoopInAdded { Height = h; LoopIn = x }, HasNotStarted ->
      In (h, x)
    | OurSwapTxPublished { TxHex = txHex; HtlcOutIndex = vOut}, In(h, x) ->
      let nextState = { x with SwapTxInfoHex = Some { TxHex = txHex; N = vOut }; }
      let cost = updateCost (In(h, nextState)) event nextState.Cost
      In (h, { nextState with Cost = cost  })
    | RefundTxPublished { TxId = txid }, In(h, x) ->
      In(h, { x with RefundTransactionId = Some txid })
    | SuccessTxConfirmed _, In(h, x) ->
      In(h, { x with IsOurSuccessTxConfirmed = true })
    | RefundTxConfirmed _, In(h, x) ->
      In(h, { x with Cost = updateCost state event x.Cost })
    | OffChainPaymentReceived _, In(h, x) ->
      In(h, { x with Cost = updateCost state event x.Cost; IsOffChainPaymentReceived = true })

    | FinishedByError { Error = err }, In(_, { Cost = cost; PairId = pairId }) ->
      let group = { Category = Category.In; PairId = pairId }
      Finished(cost, FinishedState.Errored(err), group)
    | FinishedByError { Error = err }, Out(_, { Cost = cost; PairId = pairId }) ->
      let group = { Category = Category.Out; PairId = pairId }
      Finished(cost, FinishedState.Errored(err), group)
    | FinishedSuccessfully _, Out (_ , { Cost = cost; PairId = pairId }) ->
      let group = { Category = Category.Out; PairId = pairId }
      Finished(cost, FinishedState.Success, group)
    | FinishedSuccessfully _, In(_, { Cost = cost; PairId = pairId }) ->
      let group = { Category = Category.In; PairId = pairId }
      Finished(cost, FinishedState.Success, group)
    | FinishedByRefund _, In (_h, { Cost = cost; RefundTransactionId = Some txid; PairId = pairId }) ->
      let group = { Category = Category.In; PairId = pairId }
      Finished(cost, FinishedState.Refunded(txid), group)
    | FinishedByTimeout { Reason = reason }, Out(_, { Cost = cost; PairId = pairId }) ->
      let group = { Category = Category.Out; PairId = pairId }
      Finished(cost, FinishedState.Timeout(reason), group)
    | FinishedByTimeout { Reason = reason }, In(_, { Cost = cost; PairId = pairId }) ->
      let group = { Category = Category.In; PairId = pairId }
      Finished(cost, FinishedState.Timeout(reason), group)
    | NewTipReceived { Height = h }, Out(_, x) ->
      Out(h, x)
    | NewTipReceived { Height = h }, In(_, x) ->
      In(h, x)
    | _, x -> x

  type Aggregate = Aggregate<State, Command, Event, Error, uint16 * DateTime>
  type Handler = Handler<State, Command, Event, Error, SwapId>
  type IActor = IActor<State, Command, Event, Error, SwapId, uint16 * DateTime>

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
              | Event.BlockUnConfirmed { BlockHash = blockHash } -> Some blockHash
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

  let getRepository store =
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

  let getHandler aggr store =
    getRepository store
    |> Handler.Create aggr

[<RequireQualifiedAccess>]
module SwapCost =
  let split (group: Swap.Group) (cost: SwapCost) =
    let onchainCost =
      {
        SwapCost.Zero
          with
            OnchainPayment = cost.OnchainPayment
            OnchainFee = cost.OnchainFee
      }
    let offChainCost =
      {
        SwapCost.Zero
          with
            OffchainPayment = cost.OffchainPayment
            OffchainFee = cost.OffchainFee
            OffchainPrepayment = cost.OffchainPrepayment
            OffchainPrepaymentFee = cost.OffchainPrepaymentFee
      }
    ((group.OnChainAsset, onchainCost), (group.OffChainAsset, offChainCost))

  let foldSwapStates (states: Swap.State seq) =
      states
      |> Seq.choose(fun state ->
        (match state with
        | Swap.State.HasNotStarted -> None
        | Swap.State.Out(_height, { Cost = cost; PairId = pairId }) ->
          let group = { Swap.Group.Category = Swap.Category.Out; Swap.Group.PairId = pairId }
          cost
          |> split group
          |> Some
        | Swap.State.In(_height, { Cost = cost; PairId = pairId }) ->
          let group = { Swap.Group.Category = Swap.Category.In; Swap.Group.PairId = pairId }
          cost
          |> split group
          |> Some
        | Swap.State.Finished(cost, _x, group) ->
          cost
          |> split group
          |> Some
        )
      )
      |> Seq.fold(fun acc ((onchainAsset, onchainCost), (offChainAsset, offChainCost)) ->
          acc
          |> Map.change onchainAsset (function | Some c -> Some (onchainCost + c) | None -> Some onchainCost)
          |> Map.change offChainAsset (function | Some c -> Some (offChainCost + c) | None -> Some offChainCost)
        ) Map.empty<SupportedCryptoCode, SwapCost>
