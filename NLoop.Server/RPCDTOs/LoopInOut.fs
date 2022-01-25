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
      elif l.StartsWith Labels.reservedPrefix then
        Error $"{Labels.reservedPrefix} is a reserved prefix"
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

  [<JsonPropertyName "last_hop">]
  LastHop: PubKey option

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

  member this.Validate() =
    this.HtlcConfTarget
    |> checkConfTarget
    |> Result.mapError(fun e -> [e])


type LoopOutLimits = {
  MaxPrepay: Money
  MaxSwapFee: Money
  MaxRoutingFee: Money
  MaxPrepayRoutingFee: Money
  MaxMinerFee: Money
  MaxCLTVDelta: BlockHeightOffset32
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
  [<JsonPropertyName "channel_ids">]
  ChannelIds: ShortChannelId array voption
  /// The address which counterparty must pay.
  /// If none, the daemon should query a new one from LND.
  /// (or, for assets those which does not support off-chain,
  /// it asks a blockchain daemon's (e.g. litecoind) wallet for new address.)
  [<JsonPropertyName "address">]
  Address: string option

  [<JsonPropertyName "pair_id">]
  PairId: PairId option

  [<JsonPropertyName "amount">]
  Amount: Money

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
  member this.OutgoingChannelIds =
    this.ChannelIds |> ValueOption.toArray |> Array.concat

  static member Instance = {
    ChannelIds = ValueNone
    Address = None
    PairId = None
    Amount = Money.Zero
    SwapTxConfRequirement = None
    Label = None
    MaxSwapRoutingFee = ValueNone
    MaxPrepayRoutingFee = ValueNone
    MaxSwapFee = ValueNone
    MaxPrepayAmount = ValueNone
    MaxMinerFee = ValueNone
    SweepConfTarget = ValueNone
  }
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
      MaxCLTVDelta = d.MaxCLTVDelta
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
      | Some a ->
        let n = getNetwork(onChain)
        try
          BitcoinAddress.Create(a, n) |> ignore
          Ok()
        with
        | ex ->
          Error $"Invalid address, on-chain address must be the one for network: {getNetwork(onChain)}. It was {n}: {ex}"
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
