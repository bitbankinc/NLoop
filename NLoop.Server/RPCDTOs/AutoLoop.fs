namespace NLoop.Server.RPCDTOs

open NLoop.Domain
open System.Text.Json.Serialization
open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Domain.IO
open NLoop.Server
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
  | LoopOutUnreachable of errorMsg: string
  | LoopInUnReachable of errorMsg: string
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
  [<JsonPropertyName "incoming_threshold_percent">]
  IncomingThreshold: int16<percent>

  [<JsonPropertyName "outgoing_threshold_percent">]
  OutgoingThreshold: int16<percent>
  // -----  -----
}
type LiquidityParameters = {
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
  [<JsonPropertyName "min_swap_amount">]
  MinSwapAmount: Money option
  [<JsonPropertyName "max_swap_amount">]
  MaxSwapAmount: Money option

  [<JsonPropertyName "htlc_conf_target">]
  HTLCConfTarget: int option
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
  ChannelId: ShortChannelId voption
  [<JsonPropertyName "pubkey">]
  PubKey: PubKey voption
  [<JsonPropertyName "reason">]
  Reason: string
}
