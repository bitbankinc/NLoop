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
  [<JsonPropertyName "server_onchain">]
  ServerOnChain: Money

  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  [<JsonPropertyName "server_offchain">]
  ServerOffChain: Money

  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  [<JsonPropertyName "onchain">]
  OnChain: Money

  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  [<JsonPropertyName "offchain">]
  OffChain: Money
}
  with
  member this.OnChainTotal =
    this.ServerOnChain +  this.OnChain

  member this.OffChainTotal =
    this.ServerOffChain + this.OffChain

  static member (+) (a: SwapCost, b: SwapCost) = {
    OffChain = a.OffChain + b.OffChain
    ServerOnChain = a.ServerOnChain + b.ServerOnChain
    ServerOffChain = a.ServerOffChain + b.ServerOffChain
    OnChain = a.OnChain + b.OnChain
  }

  static member Zero = {
    ServerOnChain = Money.Zero
    ServerOffChain = Money.Zero
    OnChain = Money.Zero
    OffChain = Money.Zero
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
  LockupTransactionHex: string option

  /// The height at which Lockup Tx is confirmed.
  LockupTransactionHeight: BlockHeight option

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

  member this.AcceptZeroConf =
    this.SwapTxConfRequirement = BlockHeightOffset32.Zero
  member this.IsLockupTxConfirmedEnough(currentHeight: BlockHeight) =
    this.AcceptZeroConf ||
      match this.LockupTransactionHeight with
      | None -> false
      | Some x ->
        x + this.SwapTxConfRequirement > currentHeight
  member this.LockupTransaction =
    this.LockupTransactionHex
    |> Option.map(fun txHex -> Transaction.Parse(txHex, this.BaseAssetNetwork))

  member this.Validate() =
    if this.OnChainAmount <= Money.Zero then Error "LoopOut has non-positive on chain amount" else
    Ok()

type LoopIn = {
  [<JsonConverter(typeof<SwapIdJsonConverter>)>]
  Id: SwapId

  [<JsonConverter(typeof<PrivKeyJsonConverter>)>]
  RefundPrivateKey: Key
  Preimage: PaymentPreimage option
  [<JsonConverter(typeof<ScriptJsonConverter>)>]
  RedeemScript: Script
  Invoice: string
  Address: string
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  ExpectedAmount: Money
  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight

  [<JsonConverter(typeof<BlockHeightOffsetJsonConverter>)>]
  HTLCConfTarget: BlockHeightOffset32

  LockupTransactionHex: string option
  LockupTransactionOutPoint: (uint256 * uint) option
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

  Cost: SwapCost
}
  with
  member this.BaseAssetNetwork =
    let struct (cryptoCode, _) = this.PairId.Value
    cryptoCode.ToNetworkSet().GetNetwork(this.ChainName |> ChainName)
  member this.QuoteAssetNetwork =
    let struct (_, cryptoCode) = this.PairId.Value
    cryptoCode.ToNetworkSet().GetNetwork(this.ChainName |> ChainName)

  member this.PaymentRequest =
    PaymentRequest.Parse(this.Invoice)
    |> ResultUtils.Result.deref

  member this.Validate() =
    if this.ExpectedAmount <= Money.Zero then Error $"LoopIn has non-positive expected amount {this.ExpectedAmount}" else
    Ok()
