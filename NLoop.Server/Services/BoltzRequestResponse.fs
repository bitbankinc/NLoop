namespace NLoop.Server.Services

open System
open System.Text.Json.Serialization
open DotNetLightning.Payment
open DotNetLightning.Utils
open NBitcoin
open NLoop.Server
open NLoop.Server.DTOs
open NLoop.Server.Swap

type GetVersionResponse = {
  Version: string
}
  with
  member private this.Triple = this.Version.Split(".")
  member this.Major = this.Triple.[0] |> Int32.Parse
  member this.Minor = this.Triple.[1] |> Int32.Parse
  member this.Patch = this.Triple.[2].Split("-").[0] |> Int32.Parse

type GetPairsResponse = {
  Info: string []
  Warnings: string []
  Pairs: Map<string, PairInfo>
}
and PairInfo = {
  Rate: double
  Limits: {| Maximal: int64; Minimal: int64; MaximalZeroConf: {|BaseAsset: int64; QuoteAsset: int64|} |}
  Fees: {|
           Percentage: double
           MinerFees: {| BaseAsset : AssetFeeInfo; QuoteAsset: AssetFeeInfo |}
         |}
  Hash: string
}
and AssetFeeInfo = {
  Normal: int64
  Reverse: {| Claim: int64; Lockup: int64 |}
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
    this._TimeoutEta |> Option.map NBitcoin.Utils.UnixTimeToDateTime


type SwapStatusType =
  | Created
  | InvoiceSet
  | TxMempool
  | TxConfirmed
  | InvoicePayed
  | InvoiceFailedToPay
  | TxClaimed
  | Unknown of string

type TxInfo = {
  [<JsonConverter(typeof<UInt256JsonConverter>)>]
  [<JsonPropertyName("id")>]
  TxId: uint256
  [<JsonPropertyName("hex")>]
  Tx: Transaction
  Eta: int
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
    | "swap.created" -> Created
    | "invoice.set" -> InvoiceSet
    | "transaction.mempool" -> TxMempool
    | "transaction.confirmed" -> TxConfirmed
    | "invoice.payed" -> InvoicePayed
    | "invoice.failedToPay" -> InvoiceFailedToPay
    | "transaction.claimed" -> TxClaimed
    | x -> Unknown x

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
  Address: BitcoinAddress
  AcceptZeroConf: bool
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  ExpectedAmount: Money
  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight
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
  LockupAddress: BitcoinAddress
  [<JsonConverter(typeof<PaymentRequestJsonConverter>)>]
  Invoice: PaymentRequest
  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  OnchainAmount: Money
  [<JsonConverter(typeof<ScriptJsonConverter>)>]
  RedeemScript: Script
}
  with
  member this.Validate(preimageHash: uint256, offChainAmountWePay: Money, maxSwapServiceFee: Money) =
    let actualSpk = this.LockupAddress.ScriptPubKey
    let expectedSpk = this.RedeemScript.WitHash.ScriptPubKey
    if (actualSpk <> expectedSpk) then
      Error ($"lockupAddress {this.LockupAddress} and redeem script ({this.RedeemScript}) does not match")
    else if this.Invoice.PaymentHash <> PaymentHash(preimageHash) then
      Error ("Payment Hash in invoice does not match preimage hash we specified in request")
    else if (this.Invoice.AmountValue.IsSome && this.Invoice.AmountValue.Value.Satoshi <> offChainAmountWePay.Satoshi) then
      Error ($"What they requested in invoice {this.Invoice.AmountValue.Value} does not match the amount we are expecting to pay ({offChainAmountWePay}).")
    else
    let swapServiceFee =
      offChainAmountWePay - this.OnchainAmount
    if maxSwapServiceFee < swapServiceFee then
      Error $"What swap service claimed as their fee ({swapServiceFee}) is larger than our max acceptable fee rate ({maxSwapServiceFee})"
    else (this.RedeemScript |> Scripts.validateScript)

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
