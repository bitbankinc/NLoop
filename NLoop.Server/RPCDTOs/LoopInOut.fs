namespace NLoop.Server.DTOs

open System.Text.Json.Serialization
open DotNetLightning.Utils
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.Options
open FsToolkit.ErrorHandling

[<AutoOpen>]
module private ValidationHelpers =
  let validateLabel(maybeLabel: string option) =
    match maybeLabel with
    | None -> Ok()
    | Some l ->
      if l.Length > Labels.MaxLength then
        Error $"Label's length must not be longer than {Labels.MaxLength}. it was {l.Length}"
      elif l.StartsWith Labels.reserved then
        Error $"{Labels.reserved} is a reserved prefix"
      else
        Ok()

  let inline checkConfTarget maybeConfTarget =
    match maybeConfTarget with
    | ValueNone -> Ok()
    | ValueSome x ->
      Constants.MinConfTarget <= (uint32 x)
      |> Result.requireTrue $"A confirmation target must be larger than {Constants.MinConfTarget - 1u}, it was {x}"

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

  [<JsonPropertyName "route_hints">]
  RouteHints: RouteHint array voption
}
  with
  member this.LastHop =
    let lastHops =
      this.RouteHints |> ValueOption.defaultValue [||] |> Array.map(fun rh -> rh.Hops.[0].NodeId)
    if lastHops |> Seq.distinct |> Seq.length = 1 then
      lastHops.[0] |> Some
    else
      // if we don't have unique last hop in route hints, we don't know what will be
      // the last hop. (it is up to the counterparty)
      None

  member this.PairIdValue =
    this.PairId
    |> Option.defaultValue PairId.Default

  member this.Limits =
    let defaultParameters = this.PairIdValue.DefaultLoopInParameters
    {
      MaxSwapFee =
        this.MaxSwapFee
        |> ValueOption.defaultValue(ppmToSat(this.Amount, defaultParameters.MaxSwapFeePPM))
      MaxMinerFee =
        this.MaxMinerFee
        |> ValueOption.defaultValue(defaultParameters.MaxMinerFee)
    }

  member this.SwapGroup = {
    Swap.Group.PairId = this.PairIdValue
    Swap.Group.Category = Swap.Category.In
  }

  member this.LndClientRouteHints =
    this.RouteHints
    |> ValueOption.defaultValue [||]
    |> Array.map(fun h ->
      {
        LndClient.RouteHint.Hops = h.Hops |> Array.map(fun r -> r.LndClientHopHint)
      }
    )
  member this.Validate() =
    this.HtlcConfTarget
    |> checkConfTarget
    |> Result.mapError(fun e -> [e])

and RouteHint = {
  Hops: HopHint[]
}
and HopHint = {
  [<JsonPropertyName "node_id">]
  NodeId: PubKey

  [<JsonPropertyName "chan_id">]
  ChanId: ShortChannelId

  [<JsonPropertyName "fee_base_msat">]
  FeeBaseMSat: int64

  [<JsonPropertyName "fee_proportional_millionths">]
  FeeProportionalMillionths: int64

  [<JsonPropertyName "cltv_expiry_delta">]
  CltvExpiryDelta: int
}
  with
  member this.LndClientHopHint = {
    LndClient.HopHint.NodeId = this.NodeId |> NodeId
    ShortChannelId = this.ChanId
    FeeBase = this.FeeBaseMSat |> LNMoney.MilliSatoshis
    FeeProportionalMillionths = this.FeeProportionalMillionths |> uint32
    CLTVExpiryDelta = this.CltvExpiryDelta |> uint16 |> BlockHeightOffset16
  }

type LoopOutLimits = {
  MaxPrepay: Money
  MaxSwapFee: Money
  MaxRoutingFee: Money
  MaxPrepayRoutingFee: Money
  MaxMinerFee: Money
  SwapTxConfRequirement: BlockHeightOffset32
}
  with
  /// Calculates the largest possible fees for a loop out swap,
  /// comparing the fees for a successful swap to the cost when the client pays
  /// the prepay because they failed to sweep the on chain htlc. This is unlikely,
  member this.WorstCaseFee: Money =
    let successFees = this.MaxPrepayRoutingFee + this.MaxMinerFee + this.MaxSwapFee + this.MaxRoutingFee
    let noShowFees = this.MaxPrepayRoutingFee + this.MaxPrepay
    if noShowFees > successFees then noShowFees else successFees

type LoopOutRequest = {
  [<JsonPropertyName "channel_id">]
  OutgoingChannelIds: ShortChannelId array
  /// The address which counterparty must pay.
  /// If none, the daemon should query a new one from LND.
  [<JsonPropertyName "address">]
  Address: BitcoinAddress option

  [<JsonPropertyName "pair_id">]
  PairId: PairId option

  [<JsonPropertyName "amount">]
  Amount: Money
  /// Confirmation target before we make an offer. zero-conf by default.
  [<JsonPropertyName "swap_tx_conf_requirement">]
  SwapTxConfRequirement: int option
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
    match this.SwapTxConfRequirement with
    | None
    | Some 0 -> true
    | _ -> false

  member this.Limits =
    let d = this.PairIdValue.DefaultLoopOutParameters
    {
      LoopOutLimits.MaxPrepay =
        this.MaxPrepayAmount
        |> ValueOption.defaultValue(d.MaxPrepay)
      MaxSwapFee =
        this.MaxSwapFee
        |> ValueOption.defaultValue(ppmToSat(this.Amount, d.MaxSwapFeePPM))
      MaxRoutingFee =
        this.MaxSwapRoutingFee
        |> ValueOption.defaultValue(d.MaxRoutingFee)
      MaxPrepayRoutingFee =
        this.MaxPrepayRoutingFee
        |> ValueOption.defaultValue(d.MaxPrepayRoutingFee)
      MaxMinerFee =
        this.MaxMinerFee
        |> ValueOption.defaultValue(d.MaxMinerFee)
      SwapTxConfRequirement =
        this.SwapTxConfRequirement
        |> Option.map(uint32 >> BlockHeightOffset32)
        |> Option.defaultValue(d.SwapTxConfRequirement)
    }
  member this.PairIdValue: PairId =
    this.PairId
    |> Option.defaultValue PairId.Default

  member this.SwapGroup: Swap.Group = {
    Swap.Group.PairId = this.PairIdValue
    Swap.Group.Category = Swap.Category.Out
  }
  member this.Validate(getNetwork: SupportedCryptoCode -> Network): Result<unit, string list> =
    let struct (onChain, _offChain) = this.PairIdValue.Value
    let checkAddressHasCorrectNetwork =
      match this.Address with
      | Some a when a.Network <> getNetwork(onChain) ->
        Error $"on-chain address must be the one for network: {getNetwork(onChain)}. It was {a.Network}"
      | _ ->
        Ok()

    validation {
      let! () = checkAddressHasCorrectNetwork
      and! () = checkConfTarget(this.SweepConfTarget)
      and! () = validateLabel this.Label
      return ()
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
