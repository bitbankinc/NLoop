namespace NLoop.Server

open System
open System.Runtime.CompilerServices
open System.Text.Json.Serialization
open DotNetLightning.Payment
open DotNetLightning.Utils
open NBitcoin
open NBitcoin.JsonConverters
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.DTOs
open NLoop.Domain

type GetVersionResponse = {
  Version: string
}
  with
  member private this.Triple = this.Version.Split(".")
  member this.Major = this.Triple.[0] |> Int32.Parse
  member this.Minor = this.Triple.[1] |> Int32.Parse
  member this.Patch = this.Triple.[2].Split("-").[0] |> Int32.Parse

type SwapStatusType =
  | SwapCreated = 0uy
  | SwapExpired = 1uy

  | InvoiceSet = 10uy
  | InvoicePayed = 11uy
  | InvoicePending = 12uy
  | InvoiceSettled = 13uy
  | InvoiceFailedToPay = 14uy

  | ChannelCreated = 20uy

  | TxFailed = 30uy
  | TxMempool = 31uy
  | TxClaimed = 32uy
  | TxRefunded = 33uy
  | TxConfirmed = 34uy

  | Unknown = 255uy

[<RequireQualifiedAccess>]
module SwapStatusType =
  let FromString(s) =
    match s with
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

[<AbstractClass;Sealed;Extension>]
type SwapStatusTypeExt() =
  [<Extension>]
  static member AsString(this: SwapStatusType) =
    match this with
    | SwapStatusType.SwapCreated ->
      "swap.created"
    | SwapStatusType.SwapExpired ->
      "swap.expired"
    | SwapStatusType.InvoiceSet ->
      "invoice.set"
    | SwapStatusType.InvoicePayed ->
      "invoice.payed"
    | SwapStatusType.InvoicePending ->
      "invoice.pending"
    | SwapStatusType.InvoiceSettled ->
      "invoice.settled"
    | SwapStatusType.InvoiceFailedToPay ->
      "invoice.failedToPay"

    | SwapStatusType.ChannelCreated ->
      "channel.created"

    | SwapStatusType.TxFailed ->
      "transaction.failed"
    | SwapStatusType.TxMempool ->
      "transaction.mempool"
    | SwapStatusType.TxClaimed ->
      "transaction.claimed"
    | SwapStatusType.TxRefunded ->
      "transaction.refunded"
    | SwapStatusType.TxConfirmed ->
      "transaction.confirmed"
    | _ -> "unknown"
type GetPairsResponse = {
  Info: string []
  Warnings: string []
  Pairs: Map<string, PairInfo>
}

and PairInfo = {
  Rate: double
  Limits: ServerLimit
  Fees: Fees
  Hash: string
}
and Fees = {
  Percentage: double
  MinerFees: BaseAndQuote<AssetFeeInfo>
}
and AssetFeeInfo = {
  Normal: int64
  Reverse: {| Claim: int64; Lockup: int64 |}
}
and ServerLimit = {
  Maximal: int64
  Minimal: int64
  MaximalZeroConf: BaseAndQuote<int64>
}
and BaseAndQuote<'T> = {
  BaseAsset: 'T
  QuoteAsset: 'T
}

type GetTimeOutsResponse = {
  Timeouts: Map<string, TimeoutInfo>
}
and TimeoutInfo = {
  Base: int
  Quote: int
}

type GetNodesResponse = {
  Nodes: Map<string, NodeInfo>
}
and NodeInfo = {
  NodeKey: PubKey
  Uris: PeerConnectionString []
}
type GetTxResponse = {
  [<JsonPropertyName("transactionHex")>]
  Transaction: Transaction
}

type GetSwapTxResponse = {
  [<JsonPropertyName("transactionHex")>]
  Transaction: Transaction
  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight

  [<JsonPropertyName("timeoutEta")>]
  [<JsonConverter(typeof<UnixTimeJsonConverter>)>]
  _TimeoutEta: uint option
}
  with
  member this.TimeoutEta =
    this._TimeoutEta |> Option.map Utils.UnixTimeToDateTime

type TxInfo = {
  [<JsonConverter(typeof<UInt256JsonConverter>)>]
  [<JsonPropertyName("id")>]
  TxId: uint256
  [<JsonPropertyName("hex")>]
  Tx: Transaction
  Eta: int option
}

type SwapStatusResponse = {
  [<JsonPropertyName("status")>]
  _Status: string
  Transaction: TxInfo option
  FailureReason: string option
}
  with
  member this.SwapStatus =
    match this._Status with
    | "swap.created" -> SwapStatusType.SwapCreated
    | "invoice.set" -> SwapStatusType.InvoiceSet
    | "transaction.mempool" -> SwapStatusType.TxMempool
    | "transaction.confirmed" -> SwapStatusType.TxConfirmed
    | "invoice.payed" -> SwapStatusType.InvoicePayed
    | "invoice.failedToPay" -> SwapStatusType.InvoiceFailedToPay
    | "transaction.claimed" -> SwapStatusType.TxClaimed
    | _x -> SwapStatusType.Unknown

type CreateSwapRequest = {
  [<JsonConverter(typeof<PairIdJsonConverter>)>]
  PairId: PairId
  OrderSide: OrderType
  [<JsonConverter(typeof<HexPubKeyJsonConverter>)>]
  RefundPublicKey: PubKey
  [<JsonConverter(typeof<PaymentRequestJsonConverter>)>]
  Invoice: PaymentRequest
}

type ChannelOpenRequest = {
  Private: bool
  InboundLiquidity: double
  Auto: bool
}

type CreateSwapResponse = {
  Id: string
  Address: string
  [<JsonConverter(typeof<ScriptJsonConverter>)>]
  RedeemScript: Script
  AcceptZeroConf: bool
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  ExpectedAmount: Money
  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight
}
  with
  member this.Validate(preimageHash: uint256, refundPubKey, ourInvoiceAmount: Money, maxSwapServiceFee: Money, onChainNetwork: Network): Result<_, string> =
    let mutable addr = null
    let mutable e = null
    try
      addr <-
        BitcoinAddress.Create(this.Address, onChainNetwork)
    with
    | :? FormatException as ex ->
      e <- ex
      ()
    if isNull addr then Error($"Boltz returned invalid bitcoin address ({this.Address}): error msg: {e.Message}") else
    let actualSpk = addr.ScriptPubKey
    let expectedSpk = this.RedeemScript.WitHash.ScriptPubKey
    if (actualSpk <> expectedSpk) then
      Error ($"Address {this.Address} and redeem script ({this.RedeemScript}) does not match")
    else
      let swapServiceFee =
        ourInvoiceAmount - this.ExpectedAmount
      if maxSwapServiceFee < swapServiceFee then
        Error $"What swap service claimed as their fee ({swapServiceFee}) is larger than our max acceptable fee rate ({maxSwapServiceFee})"
      else
        (this.RedeemScript |> Scripts.validateSwapScript preimageHash refundPubKey this.TimeoutBlockHeight)


type CreateChannelRequest = {
  [<JsonConverter(typeof<PairIdJsonConverter>)>]
  PairId: PairId
  OrderSide: OrderType
  [<JsonConverter(typeof<HexPubKeyJsonConverter>)>]
  RefundPublicKey: PubKey
  [<JsonConverter(typeof<PaymentRequestJsonConverter>)>]
  Invoice: PaymentRequest
  [<JsonConverter(typeof<UInt256JsonConverter>)>]
  PreimageHash: uint256
  Channel: ChannelOpenRequest
}
type CreateReverseSwapRequest = {
  [<JsonConverter(typeof<PairIdJsonConverter>)>]
  PairId: PairId
  OrderSide: OrderType
  [<JsonConverter(typeof<HexPubKeyJsonConverter>)>]
  ClaimPublicKey: PubKey
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  InvoiceAmount: Money
  [<JsonConverter(typeof<UInt256JsonConverter>)>]
  PreimageHash: uint256
}

type CreateReverseSwapResponse = {
  Id: string
  LockupAddress: string
  [<JsonConverter(typeof<PaymentRequestJsonConverter>)>]
  Invoice: PaymentRequest
  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  OnchainAmount: Money
  [<JsonConverter(typeof<ScriptJsonConverter>)>]
  RedeemScript: Script

  /// The invoice
  MinerFeeInvoice: PaymentRequest option
}
  with
  member this.Validate(preimageHash: uint256,
                       claimPubKey: PubKey,
                       offChainAmountWePay: Money,
                       maxSwapServiceFee: Money,
                       maxPrepay: Money,
                       n: Network): Result<_, string> =
    let mutable addr = null
    let mutable e = null
    try
      addr <-
        BitcoinAddress.Create(this.LockupAddress, n)
    with
    | :? FormatException as ex ->
      e <- ex
      ()
    if isNull addr then Error($"Boltz returned invalid bitcoin address for lockup address ({this.LockupAddress}): error msg: {e.Message}") else
    let actualSpk = addr.ScriptPubKey
    let expectedSpk = this.RedeemScript.WitHash.ScriptPubKey
    if (actualSpk <> expectedSpk) then
      Error $"lockupAddress {this.LockupAddress} and redeem script ({this.RedeemScript}) does not match"
    else if this.Invoice.PaymentHash <> PaymentHash(preimageHash) then
      Error "Payment Hash in invoice does not match preimage hash we specified in request"
    else if (this.Invoice.AmountValue.IsSome && this.Invoice.AmountValue.Value.Satoshi <> offChainAmountWePay.Satoshi) then
      Error $"What they requested in invoice {this.Invoice.AmountValue.Value} does not match the amount we are expecting to pay ({offChainAmountWePay})."
    else
      let prepayAmount =
        this.MinerFeeInvoice
        |> Option.bind(fun invoice -> invoice.AmountValue)
        |> Option.defaultValue LNMoney.Zero
      if prepayAmount.Satoshi > maxPrepay.Satoshi then
        Error $"The amount specified in invoice ({prepayAmount.Satoshi} sats) was larger than the max we can accept ({maxPrepay})"
      else
      let swapServiceFee =
        offChainAmountWePay.Satoshi + prepayAmount.Satoshi - this.OnchainAmount.Satoshi
        |> Money.Satoshis
      if maxSwapServiceFee < swapServiceFee then
        Error $"What swap service claimed as their fee ({swapServiceFee}) is larger than our max acceptable fee rate ({maxSwapServiceFee})"
      elif maxPrepay.Satoshi < prepayAmount.Satoshi then
        Error $"The counterparty-specified amount for prepayment miner fee ({prepayAmount}) is larger than our maximum ({maxPrepay})"
      else
        this.RedeemScript |> Scripts.validateReverseSwapScript preimageHash claimPubKey this.TimeoutBlockHeight

type GetSwapRatesResponse = {
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  InvoiceAmount: Money
}

type SetInvoiceResponse = {
  AcceptZeroConf: bool
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  ExpectedAmount: Money
  Bip21: string
}
