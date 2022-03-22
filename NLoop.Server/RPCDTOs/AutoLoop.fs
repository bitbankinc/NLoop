namespace NLoop.Server.RPCDTOs

open NLoop.Domain
open System.Text.Json.Serialization
open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.DTOs

type Targets = {
  Peers: NodeId[]
  Channels: ShortChannelId[]
}
[<RequireQualifiedAccess>]
type SwapDisqualifiedReason =
  | SweepFeesTooHigh of {| Estimation: FeeRate; OurLimit: FeeRate |}
  | InFlightLimitReached
  | SwapFeeTooHigh of {| ServerRequirement: Money; OurLimit: Money |}
  | MinerFeeTooHigh of {| ServerRequirement: Money; OurLimit: Money |}
  | PrepayTooHigh of {| ServerRequirement: Money; OurLimit: Money |}
  | FailureBackoff
  | LoopOutAlreadyInTheChannel
  | LoopInAlreadyInTheChannel
  | LiquidityOk
  | FailedToGetOnChainAddress of msg: string
  | FeePPMInsufficient of {| Required: Money; OurLimit: Money |}
  | LoopOutUnreachable of errorMsg: string
  | LoopInUnReachable of errorMsg: string
  with
  member this.Message =
    match this with
    | SweepFeesTooHigh v ->
      $"Current estimated FeeRate is {v.Estimation.SatoshiPerByte} sat/vbyte. But our limit for sweep_fee_rate is {v.OurLimit.SatoshiPerByte} sats/vbyte. You may want to set higher value for \"sweep_fee_rate_sat_per_kvbyte\""
    | MinerFeeTooHigh v  ->
      $"miner fee: ({v.ServerRequirement.Satoshi} sats) greater than our fee limit ({v.OurLimit.Satoshi} sats)"
    | SwapFeeTooHigh v ->
      $"swap fee: ({v.ServerRequirement.Satoshi} sats) greater than our fee limit ({v.OurLimit.Satoshi} sats)"
    | PrepayTooHigh v ->
      $"prepay amount: ({v.ServerRequirement.Satoshi} sats) greater than our fee limit ({v.OurLimit.Satoshi} sats)"
    | FeePPMInsufficient v ->
      $"Total required fees for the swap: ({v.Required.Satoshi} sats) greater than our fee limit ({v.OurLimit.Satoshi} sats)"
    | x -> $"{x}"

type LiquidityRuleType =
  | UNKNOWN = 0
  | THRESHOLD = 1

type LiquidityRule = {
  /// The channel id to apply this rule.
  /// PubKey and the ChannelId fields are mutually exclusive
  [<JsonPropertyName "channel_id">]
  ChannelId: ShortChannelId voption

  /// The peer id to apply this rule against.
  /// PubKey and the ChannelId fields are mutually exclusive
  [<JsonPropertyName "pubkey">]
  PubKey: PubKey voption

  [<JsonPropertyName "type">]
  Type: LiquidityRuleType

  // ----- For `LiquidityRuleType.Threshold` -----
  [<JsonPropertyName "incoming_threshold_percent">]
  IncomingThreshold: int16<percent>

  [<JsonPropertyName "outgoing_threshold_percent">]
  OutgoingThreshold: int16<percent>
  // -----  -----
}
  with
  member this.Validate() =
    if this.IncomingThreshold < 0s<percent> || this.IncomingThreshold > 100s<percent> then
      Error $"Invalid liquidity threshold {this.IncomingThreshold}"
    elif this.OutgoingThreshold < 0s<percent> || this.OutgoingThreshold > 100s<percent> then
      Error $"Invalid liquidity threshold {this.OutgoingThreshold}"
    elif this.IncomingThreshold + this.OutgoingThreshold >= 100s<percent> then
      Error $"Invalid liquidity threshold. The sum must be lower than 100 percent (incoming: {this.IncomingThreshold}). (outgoing {this.OutgoingThreshold})"
    // above validations are same with those of ThresholdRule, below are optional, we may omit in the future if there
    // are some valid use cases for setting a higher value than 40%
    elif this.IncomingThreshold > 50s<percent> then
      Error $"We don't currently allow setting value higher than 50 percent for incoming_threshold for the safety. It was {this.IncomingThreshold}"
    elif this.OutgoingThreshold > 50s<percent> then
      Error $"We don't currently allow setting value higher than 50 percent for outgoing_threshold for the safety. It was {this.OutgoingThreshold}"
    elif this.IncomingThreshold + this.OutgoingThreshold > 60s<percent> then
      let msg =
        $"We don't currently allow setting value higher than 60 percent for sum of the incoming/outgoing threshold. " +
        "It is usually a bad idea to setting such a high value for the sum. Since it will result to too-small and often " +
        "swaps. Consider making your channel larger instead."
      Error msg
    else
      Ok()
type LiquidityParameters = {
  [<JsonPropertyName "rules">]
  Rules: LiquidityRule[]

  [<JsonPropertyName "fee_ppm">]
  FeePPM: int64<ppm> voption

  [<JsonPropertyName "sweep_fee_rate_sat_per_kvbyte">]
  SweepFeeRateSatPerKVByte: Money voption
  [<JsonPropertyName "max_swap_fee_ppm">]
  MaxSwapFeePpm: int64<ppm> voption
  [<JsonPropertyName "max_routing_fee_ppm">]
  MaxRoutingFeePpm: int64<ppm> voption
  [<JsonPropertyName "max_prepay_routing_fee_ppm">]
  MaxPrepayRoutingFeePpm : int64<ppm> voption
  [<JsonPropertyName "max_prepay_sat">]
  MaxPrepay: Money voption
  [<JsonPropertyName "max_miner_fee_sat">]
  MaxMinerFee: Money voption
  [<JsonPropertyName "sweep_conf_target">]
  SweepConfTarget: int
  [<JsonPropertyName "failure_backoff_sec">]
  FailureBackoffSecond: int
  [<JsonPropertyName "autoloop">]
  AutoLoop: bool

  [<JsonPropertyName "auto_max_in_flight">]
  AutoMaxInFlight: int

  [<JsonPropertyName "min_swap_amount_loopout">]
  MinSwapAmountLoopOut: Money option
  [<JsonPropertyName "max_swap_amount_loopout">]
  MaxSwapAmountLoopOut: Money option

  [<JsonPropertyName "min_swap_amount_loopin">]
  MinSwapAmountLoopIn: Money option

  [<JsonPropertyName "max_swap_amount_loopin">]
  MaxSwapAmountLoopIn: Money option

  [<JsonPropertyName "onchain_asset">]
  OnChainAsset: SupportedCryptoCode voption

  [<JsonPropertyName "htlc_conf_target">]
  HTLCConfTarget: int option
}
  with
  member this.Targets = {
    Peers =
      this.Rules
      |> Seq.map(fun r -> r.PubKey |> ValueOption.toArray |> Array.map NodeId)
      |> Array.concat
    Channels =
      this.Rules
      |> Seq.map(fun r -> r.ChannelId |> ValueOption.toArray)
      |> Array.concat
  }

type SetLiquidityParametersRequest = {
  [<JsonPropertyName "parameters">]
  Parameters: LiquidityParameters
}

type SuggestSwapsResponse = {
  [<JsonPropertyName "loop_out">]
  LoopOut: LoopOutRequest[]
  [<JsonPropertyName "loop_in">]
  LoopIn: LoopInRequest[]
  Disqualified: Disqualified[]
}
and Disqualified = {
  [<JsonPropertyName "channel_id">]
  ChannelId: ShortChannelId voption
  [<JsonPropertyName "pubkey">]
  PubKey: PubKey voption
  [<JsonPropertyName "reason">]
  Reason: string
}
