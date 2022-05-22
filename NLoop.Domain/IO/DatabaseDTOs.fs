namespace NLoop.Domain.IO

open System
open System.Text.Json.Serialization
open DotNetLightning.Payment
open DotNetLightning.Utils
open NBitcoin
open Newtonsoft.Json
open NLoop.Domain
open NLoop.Domain.Utils


/// SwapCost is a breakdown of the final swap costs.
type SwapCost = {
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  [<JsonPropertyName "onchain_payment">]
  OnchainPayment: Money

  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  [<JsonPropertyName "offchain_payment">]
  OffchainPayment: Money

  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  [<JsonPropertyName "onchain_fee">]
  OnchainFee: Money

  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  [<JsonPropertyName "offchain_fee">]
  OffchainFee: Money

  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  [<JsonPropertyName "offchain_prepayment">]
  OffchainPrepayment: Money

  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  [<JsonPropertyName "offchain_prepayment_fee">]
  OffchainPrepaymentFee: Money
}
  with
  member this.OnChainTotal =
    this.OnchainPayment +  this.OnchainFee

  member this.OffChainTotal =
    this.OffchainPayment + this.OffchainFee + this.OffchainPrepayment + this.OffchainPrepaymentFee

  static member (+) (a: SwapCost, b: SwapCost) = {
    OffchainFee = a.OffchainFee + b.OffchainFee
    OnchainPayment = a.OnchainPayment + b.OnchainPayment
    OffchainPayment = a.OffchainPayment + b.OffchainPayment
    OnchainFee = a.OnchainFee + b.OnchainFee
    OffchainPrepayment = a.OffchainPrepayment + b.OffchainPrepayment
    OffchainPrepaymentFee = a.OffchainPrepaymentFee + b.OffchainPrepaymentFee
  }

  static member Zero = {
    OnchainPayment = Money.Zero
    OffchainPayment = Money.Zero
    OnchainFee = Money.Zero
    OffchainFee = Money.Zero
    OffchainPrepayment = Money.Zero
    OffchainPrepaymentFee = Money.Zero
  }
type LoopOut = {
  [<JsonConverter(typeof<SwapIdJsonConverter>)>]
  Id: SwapId

  OutgoingChanIds: ShortChannelId []

  [<JsonConverter(typeof<BlockHeightOffsetJsonConverter>)>]
  SwapTxConfRequirement: BlockHeightOffset32

  [<JsonConverter(typeof<PrivKeyJsonConverter>)>]
  ClaimKey: Key
  [<JsonConverter(typeof<PaymentPreimageJsonConverter>)>]
  Preimage: PaymentPreimage
  [<JsonConverter(typeof<ScriptJsonConverter>)>]
  RedeemScript: Script
  Invoice: string
  ClaimAddress: string

  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  OnChainAmount: Money
  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight

  /// a.k.a. "swap tx", "htlc tx" created and published by the counter party
  SwapTxHex: string option

  /// The height at which Swap Tx is confirmed.
  SwapTxHeight: BlockHeight option

  ClaimTransactionId: uint256 option
  IsClaimTxConfirmed: bool
  IsOffchainOfferResolved: bool

  [<JsonConverter(typeof<PairIdJsonConverter>)>]
  PairId: PairId
  Label: string

  PrepayInvoice: string

  [<JsonConverter(typeof<BlockHeightOffsetJsonConverter>)>]
  SweepConfTarget: BlockHeightOffset32

  MaxMinerFee: Money
  ChainName: string
  Cost: SwapCost
}
  with
  member this.BaseAssetNetwork =
    let struct (cryptoCode, _) = this.PairId.Value
    cryptoCode.ToNetworkSet().GetNetwork(this.ChainName |> ChainName)
  member this.QuoteAssetNetwork =
    let struct (_, cryptoCode) = this.PairId.Value
    cryptoCode.ToNetworkSet().GetNetwork(this.ChainName |> ChainName)

  member this.PossibleLockupAddress =
    seq [
      this.RedeemScript.WitHash.GetAddress(this.BaseAssetNetwork)
      this.RedeemScript.WitHash.ScriptPubKey.Hash.GetAddress(this.BaseAssetNetwork) |> unbox
    ]

  member this.AcceptZeroConf =
    this.SwapTxConfRequirement = BlockHeightOffset32.Zero
  member this.IsSwapTxConfirmedEnough(currentHeight: BlockHeight) =
    this.AcceptZeroConf ||
      match this.SwapTxHeight with
      | None -> false
      | Some x ->
        x + this.SwapTxConfRequirement - BlockHeightOffset32.One <= currentHeight
  member this.SwapTx =
    this.SwapTxHex
    |> Option.map(fun txHex -> Transaction.Parse(txHex, this.BaseAssetNetwork))

  member this.Validate() =
    if this.OnChainAmount <= Money.Zero then Error "LoopOut has non-positive on chain amount" else
    Ok()

type TxOutInfoHex = {
  TxHex: string
  N: uint
}
type LoopIn = {
  [<JsonConverter(typeof<SwapIdJsonConverter>)>]
  Id: SwapId

  [<JsonConverter(typeof<PrivKeyJsonConverter>)>]
  RefundPrivateKey: Key
  Preimage: PaymentPreimage option
  [<JsonConverter(typeof<ScriptJsonConverter>)>]
  RedeemScript: Script
  Invoice: string

  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  ExpectedAmount: Money

  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight

  [<JsonConverter(typeof<BlockHeightOffsetJsonConverter>)>]
  HTLCConfTarget: BlockHeightOffset32

  SwapTxInfoHex: TxOutInfoHex option
  RefundTransactionId: uint256 option

  [<JsonConverter(typeof<PairIdJsonConverter>)>]
  PairId: PairId
  Label: string
  ChainName: string

  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  MaxMinerFee: Money

  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  MaxSwapFee: Money

  LastHop: PubKey option

  IsOffChainPaymentReceived: bool
  IsTheirSuccessTxConfirmed: bool

  AddressType: SwapAddressType

  Cost: SwapCost
}
  with
  member this.BaseAssetNetwork =
    let struct (cryptoCode, _) = this.PairId.Value
    cryptoCode.ToNetworkSet().GetNetwork(this.ChainName |> ChainName)
  member this.QuoteAssetNetwork =
    let struct (_, cryptoCode) = this.PairId.Value
    cryptoCode.ToNetworkSet().GetNetwork(this.ChainName |> ChainName)

  member this.SwapAddress =
    match this.AddressType with
    | SwapAddressType.P2WSH ->
      this.RedeemScript.WitHash.GetAddress(this.QuoteAssetNetwork)
    | SwapAddressType.P2SH_P2WSH ->
      this.RedeemScript.WitHash.ScriptPubKey.Hash.GetAddress(this.QuoteAssetNetwork) |> unbox
    | x -> failwith $"Unreachable! Unknown address type: {x}"

  member this.PaymentRequest =
    PaymentRequest.Parse(this.Invoice)
    |> ResultUtils.Result.deref

  member this.SwapTxInfo =
    this.SwapTxInfoHex
    |> Option.map(fun info -> let tx = Transaction.Parse(info.TxHex, this.QuoteAssetNetwork) in (tx, info.N))

  member this.Validate() =
    if this.ExpectedAmount <= Money.Zero then Error $"LoopIn has non-positive expected amount {this.ExpectedAmount}" else
    Ok()
