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

type LoopOutRequest = {
  [<JsonPropertyName "channel_id">]
  ChannelId: ShortChannelId option
  /// The address which counterparty must pay.
  /// If none, the daemon should query a new one from LND.
  [<JsonPropertyName "address">]
  Address: BitcoinAddress option

  [<JsonPropertyName "pair_id">]
  PairId: PairId option

  [<JsonPropertyName "amount">]
  Amount: Money
  /// Confirmation target before we make an offer. zero-conf by default.
  [<JsonPropertyName "conf_target">]
  ConfTarget: int option
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
  member this.OutgoingChanSet =
    this.ChannelId |> Option.toArray

  member this.AcceptZeroConf =
    match this.ConfTarget with
    | None
    | Some 0 -> true
    | _ -> false

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
