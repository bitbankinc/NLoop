namespace NLoop.Server.RPCDTOs

open NLoop.Domain
open System.Text.Json.Serialization
open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Domain.IO
open NLoop.Server.DTOs

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
  | FeePPMInsufficient of {| Required: Money; OurLimit: Money |}
  with
  member this.Message =
    match this with
    | SweepFeesTooHigh v ->
      $"Current estimated FeeRate is {v.Estimation.SatoshiPerByte |> int64} sat/vbyte. But our limit is {v.OurLimit.SatoshiPerByte |> int64} sats/vbyte"
    | MinerFeeTooHigh v  ->
      $"miner fee: {v.ServerRequirement} greater than our fee limit {v.OurLimit}"
    | SwapFeeTooHigh v ->
      $"swap fee: {v.ServerRequirement} greater than our fee limit {v.OurLimit}"
    | PrepayTooHigh v ->
      $"prepay amount: {v.ServerRequirement} greater than our fee limit {v.OurLimit}"
    | FeePPMInsufficient v ->
      $"Total required fees for the swap: ({v.Required}) greater than our fee limit ({v.OurLimit})"
    | x -> $"{x}"

type LiquidityRuleType =
  | Threshold
  | UnKnown
type LiquidityRule = {
  /// PubKey and the ChannelId fields are mutually exclusive
  [<JsonPropertyName "channel_id">]
  ChannelId: ShortChannelId
  /// PubKey and the ChannelId fields are mutually exclusive
  [<JsonPropertyName "pubkey">]
  PubKey: PubKey

  Type: LiquidityRuleType

  // ----- For `LiquidityRuleType.Threshold` -----
  [<JsonPropertyName "incoming_threshold">]
  IncomingThreshold: Money

  [<JsonPropertyName "outgoing_threshold">]
  OutgoingThreshold: Money
  // -----  -----
}
type LiquidityParameters = {
  Rules: LiquidityRule[]

  [<JsonPropertyName "fee_ppm">]
  FeePPM: uint64

  [<JsonPropertyName "sweep_fee_rate_sat_per_vbyte">]
  SweepFeeRateSatPerVByte: Money
  [<JsonPropertyName "max_swap_fee_ppm">]
  MaxSwapFeePpm: uint64
  [<JsonPropertyName "max_routing_fee_ppm">]
  MaxRoutingFeePpm: uint64
  [<JsonPropertyName "max_prepay_routing_fee_ppm">]
  MaxPrepayRoutingFeePpm : uint64
  [<JsonPropertyName "max_prepay_sat">]
  MaxPrepay: Money
  [<JsonPropertyName "max_miner_fee_sat">]
  MaxMinerFee: Money
  [<JsonPropertyName "sweep_conf_target">]
  SweepConfTarget: int
  [<JsonPropertyName "failure_backoff_sec">]
  FailureBackoffSecond: int
  [<JsonPropertyName "autoloop">]
  AutoLoop: bool

  [<JsonPropertyName "auto_max_in_flight">]
  AutoMaxInFlight: int
  [<JsonPropertyName "min_swap_amount">]
  MinSwapAmount: Money
  [<JsonPropertyName "max_swap_amount">]
  MaxSwapAmount: Money

  [<JsonPropertyName "pair_id">]
  PairId: PairId
}

type SetLiquidityParametersRequest = {
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
  ChannelId: ShortChannelId
  [<JsonPropertyName "pubkey">]
  PubKey: PubKey
  [<JsonPropertyName "reason">]
  Reason: string
}
