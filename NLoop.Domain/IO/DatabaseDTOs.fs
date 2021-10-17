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
  Server: Money
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  OnChain: Money
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  OffChain: Money
}
  with
  member this.Total =
    this.Server + this.OnChain + this.OffChain
  static member Zero = {
    Server = Money.Zero
    OnChain = Money.Zero
    OffChain = Money.Zero
  }
type LoopOut = {
  [<JsonConverter(typeof<SwapIdJsonConverter>)>]
  Id: SwapId
  [<JsonConverter(typeof<SwapStatusTypeJsonConverter>)>]
  Status: SwapStatusType

  AcceptZeroConf: bool

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

  LockupTransactionHex: string option

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
    let struct (cryptoCode, _) = this.PairId
    cryptoCode.ToNetworkSet().GetNetwork(this.ChainName |> ChainName)
  member this.QuoteAssetNetwork =
    let struct (_, cryptoCode) = this.PairId
    cryptoCode.ToNetworkSet().GetNetwork(this.ChainName |> ChainName)

  member this.LockupTransaction =
    this.LockupTransactionHex
    |> Option.map(fun txHex -> Transaction.Parse(txHex, this.BaseAssetNetwork))

  member this.Validate() =
    if this.OnChainAmount <= Money.Zero then Error ("LoopOut has non-positive on chain amount") else
    Ok()

type LoopIn = {
  [<JsonConverter(typeof<SwapIdJsonConverter>)>]
  Id: SwapId
  [<JsonConverter(typeof<SwapStatusTypeJsonConverter>)>]
  Status: SwapStatusType

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
  RefundTransactionId: uint256 option

  [<JsonConverter(typeof<PairIdJsonConverter>)>]
  PairId: PairId
  Label: string
  ChainName: string

  Cost: SwapCost
}
  with
  member this.BaseAssetNetwork =
    let struct (cryptoCode, _) = this.PairId
    cryptoCode.ToNetworkSet().GetNetwork(this.ChainName |> ChainName)
  member this.QuoteAssetNetwork =
    let struct (_, cryptoCode) = this.PairId
    cryptoCode.ToNetworkSet().GetNetwork(this.ChainName |> ChainName)

  member this.Validate() =
    if this.ExpectedAmount <= Money.Zero then Error ($"LoopIn has non-positive expected amount {this.ExpectedAmount}") else
    Ok()
