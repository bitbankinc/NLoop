namespace NLoop.Server.DTOs

open System.Text.Json.Serialization
open DotNetLightning.Utils
open DotNetLightning.Utils.Primitives
open Giraffe
open Giraffe.HttpStatusCodeHandlers
open Giraffe.ModelValidation
open NBitcoin
open NLoop.Domain
open NLoop.Server

type LoopInLimits = {
  MaxSwapFee: Money
  MaxMinerFee: Money
}
type LoopInRequest = {
  [<JsonPropertyName "amount">]
  Amount: Money
  [<JsonPropertyName "channel_id">]
  ChannelId: ShortChannelId option
  Label: string option

  [<JsonPropertyName "pair_id">]
  PairId: PairId option

  [<JsonPropertyName "max_miner_fee">]
  MaxMinerFee: Money voption

  [<JsonPropertyName "max_swap_fee">]
  MaxSwapFee: Money voption

  [<JsonPropertyName "htlc_conf_target">]
  HtlcConfTarget: int voption
}
  with
  member this.Limits = {
    MaxSwapFee =
      this.MaxSwapFee |> ValueOption.defaultToVeryHighFee
    MaxMinerFee =
      this.MaxMinerFee |> ValueOption.defaultToVeryHighFee
  }

type LoopOutLimits = {
  MaxPrepay: Money
  MaxSwapFee: Money
  MaxRoutingFee: Money
  MaxPrepayRoutingFee: Money
  MaxMinerFee: Money
  HTLCConfTarget: BlockHeightOffset32
}

type LoopOutRequest = {
  [<JsonPropertyName "channel_id">]
  ChannelId: ShortChannelId array
  /// The address which counterparty must pay.
  /// If none, the daemon should query a new one from LND.
  [<JsonPropertyName "address">]
  Address: BitcoinAddress option

  [<JsonPropertyName "pair_id">]
  PairId: PairId option

  [<JsonPropertyName "amount">]
  Amount: Money
  /// Confirmation target before we make an offer. zero-conf by default.
  [<JsonPropertyName "htlc_conf_target">]
  HtlcConfTarget: int option
  Label: string option

  [<JsonPropertyName "max_swap_routing_fee">]
  MaxSwapRoutingFee: Money voption

  [<JsonPropertyName "max_prepay_routing_fee">]
  MaxPrepayRoutingFee: Money voption

  [<JsonPropertyName "max_swap_fee">]
  MaxSwapFee: Money voption

  [<JsonPropertyName "max_prepay_amount">]
  MaxPrepayAmount: Money voption

  [<JsonPropertyName "max_miner_fee">]
  MaxMinerFee: Money voption

  [<JsonPropertyName "sweep_conf_target">]
  SweepConfTarget: int voption
}
  with

  member this.AcceptZeroConf =
    match this.HtlcConfTarget with
    | None
    | Some 0 -> true
    | _ -> false

  member this.Limits = {
    MaxPrepay =
      this.MaxPrepayAmount |> ValueOption.defaultToVeryHighFee
    MaxSwapFee =
      this.MaxSwapFee |> ValueOption.defaultToVeryHighFee
    MaxRoutingFee =
      this.MaxSwapRoutingFee |> ValueOption.defaultToVeryHighFee
    MaxPrepayRoutingFee =
      this.MaxPrepayRoutingFee |> ValueOption.defaultToVeryHighFee
    MaxMinerFee =
      this.MaxMinerFee |> ValueOption.defaultToVeryHighFee
    HTLCConfTarget =
      this.HtlcConfTarget
      |> Option.defaultValue(Constants.DefaultHtlcConf)
      |> uint32 |> BlockHeightOffset32
  }

type LoopInResponse = {
  /// Unique id for the swap.
  [<JsonPropertyName "id">]
  Id: string
  /// An address to which we have paid.
  [<JsonPropertyName "address">]
  Address: string
}

type LoopOutResponse = {
  /// Unique id for the swap.
  [<JsonPropertyName "id">]
  Id: string
  /// An address to which counterparty paid.
  [<JsonPropertyName "address">]
  Address: string
  /// txid by which they have paid to us. It might be null when it is not 0-conf.
  [<JsonPropertyName "claim_tx_id">]
  ClaimTxId: uint256 option
}
