namespace NLoop.Server

open System
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
    this._TimeoutEta |> Option.map Utils.UnixTimeToDateTime

type TxInfo = {
  [<JsonConverter(typeof<UInt256JsonConverter>)>]
  [<JsonPropertyName("id")>]
  TxId: uint256
  [<JsonPropertyName("hex")>]
  Tx: Transaction
  Eta: int
}
  with
  member this.ToDomain = {
    Swap.Data.TxInfo.TxId = this.TxId
    Swap.Data.TxInfo.Tx = this.Tx
    Swap.Data.TxInfo.Eta = this.Eta
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
    | "swap.created" -> SwapStatusType.Created
    | "invoice.set" -> SwapStatusType.InvoiceSet
    | "transaction.mempool" -> SwapStatusType.TxMempool
    | "transaction.confirmed" -> SwapStatusType.TxConfirmed
    | "invoice.payed" -> SwapStatusType.InvoicePayed
    | "invoice.failedToPay" -> SwapStatusType.InvoiceFailedToPay
    | "transaction.claimed" -> SwapStatusType.TxClaimed
    | _x -> SwapStatusType.Unknown

  member this.ToDomain =
    { Swap.Data.SwapStatusResponseData._Status = this._Status
      Swap.Data.SwapStatusResponseData.Transaction = this.Transaction |> Option.map(fun t -> t.ToDomain)
      Swap.Data.SwapStatusResponseData.FailureReason = this.FailureReason }

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
  [<JsonConverter(typeof<ScriptJsonConverter>)>]
  RedeemScript: Script
  AcceptZeroConf: bool
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  ExpectedAmount: Money
  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight
}
  with
  member this.Validate(preimageHash: uint256, ourInvoiceAmount: Money, maxSwapServiceFee: Money): Result<_, string> =
    let actualSpk = this.Address.ScriptPubKey
    let expectedSpk = this.RedeemScript.WitHash.ScriptPubKey
    if (actualSpk <> expectedSpk) then
      Error ($"Address {this.Address} and redeem script ({this.RedeemScript}) does not match")
    else
      let swapServiceFee =
        ourInvoiceAmount - this.ExpectedAmount
      if maxSwapServiceFee < swapServiceFee then
        Error $"What swap service claimed as their fee ({swapServiceFee}) is larger than our max acceptable fee rate ({maxSwapServiceFee})"
      else
        (this.RedeemScript |> Scripts.validateSwapScript preimageHash)


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
  member this.Validate(preimageHash: uint256, offChainAmountWePay: Money, maxSwapServiceFee: Money): Result<_, string> =
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
      else
        (this.RedeemScript |> Scripts.validateReverseSwapScript preimageHash)

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
