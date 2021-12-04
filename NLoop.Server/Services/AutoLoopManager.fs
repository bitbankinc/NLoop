namespace NLoop.Server.Services

open System
open System.Collections.Generic
open System.Linq
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Chain
open DotNetLightning.Utils
open FsToolkit.ErrorHandling
open LndClient
open Microsoft.Extensions.Hosting
open FSharp.Control.Tasks
open Microsoft.Extensions.Internal
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Microsoft.Extensions.DependencyInjection
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.DTOs
open NLoop.Server.Projections
open NLoop.Server.RPCDTOs
open NLoop.Server.SwapServerClient

[<AutoOpen>]
module internal AutoLoopConstants =
  /// We use static fee rate to estimate our sweep fee, because we can't realistically
  /// estimate what our fee estimate will be by the time we reach timeout. We set this to a
  /// high estimate so that we can account for worst-case fees, (1250 * 4 / 1000) = 50 sat/byte
  let defaultLoopInSweepFee = FeeRate(1250m)

  let defaultBudget =
    DotNetLightning.Channel.ChannelConstants.MAX_FUNDING_SATOSHIS
  let [<Literal>] defaultFeePPM =
    20000L<ppm>
  /// default number of the upper limit of the on-going swap number.
  let defaultMaxInFlight = 1

  let defaultFailureBackoff = TimeSpan.FromHours(24.)

  let tick = TimeSpan.FromSeconds(20.)

[<AutoOpen>]
module internal AutoLoopHelpers =

  let splitOffChain(available: Money, prepayAmount: Money, swapAmount: Money) =
    let total = swapAmount + prepayAmount
    let prepayMaxFee = ((available.Satoshi * prepayAmount.Satoshi) / total.Satoshi) |> Money.Satoshis
    let routeMaxFee = ((available.Satoshi * swapAmount.Satoshi) / total.Satoshi) |> Money.Satoshis
    prepayMaxFee, routeMaxFee
  let private getChanInfos (lnClient: INLoopLightningClient) (cId: ShortChannelId) = task {
      let! resp = lnClient.GetChannelInfo(cId)
      return (resp, cId)
    }
  let private chanInfoToRouteHint(c, cId) =
    {
      HopHint.NodeId = c.Node1Policy.Id
      HopHint.ChanId = cId
      FeeBaseMSat = c.Node1Policy.FeeBase.MilliSatoshi
      FeeProportionalMillionths = c.Node1Policy.FeeProportionalMillionths.MilliSatoshi
      CltvExpiryDelta = c.Node1Policy.TimeLockDelta.Value |> int
    }
    |> fun h -> { RouteHint.Hops = [|h|] }

  let chanIdToRouteHint cli =
    getChanInfos cli >> Task.map(chanInfoToRouteHint)

  let scaleMinerFee (fee: Money) =
    100 * fee

  /// Calculates the largest possible fees for a loop out swap,
  /// comparing the fees for a successful swap to the cost when the client pays
  /// the prepay because they failed to sweep the on chain htlc. This is unlikely,
  let worstCaseOutFees
    ({ LoopOutLimits.MaxPrepayRoutingFee = maxPrepayRoutingFee
       MaxSwapFee = maxSwapFee
       MaxRoutingFee = maxSwapRoutingFee
       MaxMinerFee = maxMinerFee
       MaxPrepay = maxPrepayAmount
        }) : Money =
    let successFees = maxPrepayRoutingFee + maxMinerFee + maxSwapFee + maxSwapRoutingFee
    let noShowFees = maxPrepayRoutingFee + maxPrepayAmount
    if noShowFees > successFees then noShowFees else successFees

  let worstCaseInFees
    ({ LoopInLimits.MaxMinerFee = maxMinerFee
       MaxSwapFee = swapFee }) (sweepFeeEst: FeeRate) =
    let failureFee = maxMinerFee + Transactions.dummyRefundTxFee sweepFeeEst
    let successFee = maxMinerFee + swapFee
    Money.Max(failureFee, successFee)

[<Struct>]
/// minimum incoming and outgoing liquidity threshold
type ThresholdRule = {
  MinimumIncoming: int16<percent>
  MinimumOutGoing: int16<percent>
}
  with
  member this.Validate() =
    if this.MinimumIncoming < 0s<percent> || this.MinimumIncoming > 100s<percent> then
      Error $"Invalid liquidity threshold {this}"
    elif this.MinimumOutGoing < 0s<percent> || this.MinimumOutGoing > 100s<percent> then
      Error $"Invalid liquidity threshold {this}"
    elif this.MinimumIncoming + this.MinimumOutGoing >= 100s<percent> then
      Error $"Invalid liquidity threshold sum {this}"
    else
      Ok()

[<RequireQualifiedAccess>]
type AutoLoopError =
  | NegativeBudget
  | ZeroInFlight
  | NoRules
  | RestrictionError of RestrictionError
  | ExclusiveRules
  | FailedToDispatchLoop of msg: string
  with
  member this.Message =
    match this with
    | NegativeBudget -> "SwapBudget must be >= 0"
    | ZeroInFlight -> "max in flight swap must be >= 0"
    | NoRules -> "No rules set for autoloop"
    | RestrictionError r -> r.Message
    | ExclusiveRules ->
      $"channel and peer rules must be exclusive"
    | FailedToDispatchLoop msg -> msg

[<RequireQualifiedAccess>]
type SwapSuggestion =
  | Out of LoopOutRequest
  | In of LoopInRequest
  with
  member this.Fees(): Money =
    match this with
    | Out req ->
      req.Limits
      |> worstCaseOutFees
    | In req ->
      worstCaseInFees
        req.Limits
        defaultLoopInSweepFee

  member this.Amount: Money =
    match this with
    | Out req ->
      req.Amount
    | In req -> req.Amount

  member this.Channels: ShortChannelId [] =
    match this with
    | Out req ->
      req.OutgoingChannelIds
    | In _ -> [||]

  member this.Peers(knownChannels: Map<ShortChannelId, NodeId>, logger: ILogger): NodeId[] =
    match this with
    | Out req ->
      let knownPeers, unKnownPeers =
        req.OutgoingChannelIds
        |> Array.partition(fun c -> knownChannels.Any(fun kc -> kc.Key = c))

      unKnownPeers
      |> Array.iter(fun c -> logger.LogWarning($"peer for channel: {c} (%d{c.ToUInt64()}) unknown"))

      knownPeers
      |> Array.map(fun shortChannelId -> knownChannels.TryGetValue(shortChannelId) |> snd)
    | In req ->
      req.LastHop |> Option.map(NodeId) |> Option.toArray

type SwapSuggestions = {
  OutSwaps: LoopOutRequest list
  InSwaps: LoopInRequest list
  DisqualifiedChannels: Map<ShortChannelId, SwapDisqualifiedReason>
  DisqualifiedPeers: Map<NodeId, SwapDisqualifiedReason>
}
  with
  static member Zero = {
    OutSwaps = []
    InSwaps = []
    DisqualifiedChannels = Map.empty
    DisqualifiedPeers = Map.empty
  }

  member this.AddSuggestion(s: SwapSuggestion) =
    match s with
    | SwapSuggestion.Out req ->
      { this with OutSwaps = req::this.OutSwaps }
    | SwapSuggestion.In req ->
      { this with InSwaps = req::this.InSwaps }


type Balances = {
  CapacitySat: Money
  IncomingSat: Money
  OutGoingSat: Money
  /// This may be more than one channel if we are examining a balance against a peer.
  Channels: ShortChannelId ResizeArray
  PubKey: NodeId
}
  with
  static member (+) (a: Balances, b: Balances) =
    assert(a.PubKey.Equals(b.PubKey))
    {
      Balances.CapacitySat = a.CapacitySat + b.CapacitySat
      IncomingSat = a.IncomingSat + b.IncomingSat
      OutGoingSat = a.OutGoingSat + b.OutGoingSat
      Channels =
        a.Channels.AddRange(b.Channels)
        a.Channels
      PubKey = a.PubKey
    }
  static member FromLndResponse (resp: ListChannelResponse) =
    {
      CapacitySat = resp.Cap
      OutGoingSat = resp.LocalBalance
      IncomingSat = resp.Cap - resp.LocalBalance
      Channels =
        let r = ResizeArray()
        resp.Id |> r.Add
        r
      PubKey = resp.NodeId |> NodeId
    }

type private ExistingAutoLoopSummary = {
  SpentFees: Money
  PendingFees: Money
}
  with
  member summary.ValidateAgainstBudget(budget: Money, logger: ILogger) =
    if summary.TotalFees >= budget then
      logger.LogDebug($"autoloop fee budget: %d{budget.Satoshi} sats exhausted, "+
                      $"%d{summary.SpentFees.Satoshi} sats spent on completed swaps, %d{summary.PendingFees.Satoshi} sats reserved for ongoing swaps " +
                      "(upper limit)")
      Error (SwapDisqualifiedReason.BudgetElapsed)
    else
      Ok()
  member this.TotalFees =
    this.SpentFees + this.PendingFees

  static member Default = {
    SpentFees = Money.Zero
    PendingFees = Money.Zero
  }
  static member FromLoopInOuts(loopIns: LoopIn seq, loopOuts: LoopOut seq): ExistingAutoLoopSummary =
    let loopIns =
      loopIns
      |> Seq.filter(fun i -> i.Label = Labels.autoLoopLabel(Swap.Category.In))

    let loopOutFees =
      loopOuts
      |> Seq.filter(fun o -> o.Label = Labels.autoLoopLabel(Swap.Category.Out))
      |> Seq.map(fun s -> s.Cost.Total)
      |> Seq.fold(fun acc s ->
        { acc with SpentFees = acc.SpentFees + s }
      ) ExistingAutoLoopSummary.Default
    loopOutFees


[<AutoOpen>]
module private Extensions =

  type ThresholdRule with
    /// SwapAmount suggests a swap based on the liquidity thresholds configured,
    /// returning zero if no swap is recommended.
    member this.SwapAmount(channelBalances: Balances, outRestrictions: Restrictions, targetIncomingLiquidityRatio: int16<percent>): Money =
      /// The logic defined in here resembles that of the lightning loop.
      /// In lightning loop, it targets the midpoint of the the largest/smallest possible incoming liquidity.
      /// But with one difference, in lightning loop, it always targets the midpoint and there is no other choice.
      /// But in nloop, we can specify targetIncomingThresholdRatio.
      /// Thus we can tell the algorithm how we think the future incoming/outgoing payment is skewed.
      let loopOutSwapAmount (incomingThresholdPercent: int16<percent>) (outgoingThresholdPercent: int16<percent>) =
        let minimumInComing =
          (channelBalances.CapacitySat.Satoshi * int64 incomingThresholdPercent) / 100L
          |> Money.Satoshis
        let minimumOutGoing =
          (channelBalances.CapacitySat.Satoshi * int64 outgoingThresholdPercent) / 100L
          |> Money.Satoshis
        // if we have sufficient incoming capacity, we do not need to loop out.
        if channelBalances.IncomingSat >= minimumInComing then Money.Zero else
        // if we are already below the threshold set for outgoing capacity, we cannot take any further action.
        if channelBalances.OutGoingSat <= minimumOutGoing then Money.Zero else
        let targetPoint =
          let maximumIncoming = channelBalances.CapacitySat - minimumOutGoing
          let possibleTargetRange = (minimumInComing + maximumIncoming)
          (possibleTargetRange * int64 targetIncomingLiquidityRatio) / 100L
        // Calculate the amount of incoming balance we need to shift to reach this desired point.
        let required = targetPoint - channelBalances.IncomingSat
        // Since we can have pending htlcs on our channel, we check the amount of
        // outbound capacity that we can shift before we fall below our threshold.
        let available = channelBalances.OutGoingSat - minimumOutGoing
        if available < required then Money.Zero else required

      let amount = loopOutSwapAmount this.MinimumIncoming this.MinimumOutGoing
      if amount < outRestrictions.Minimum then Money.Zero else
      if outRestrictions.Maximum < amount then outRestrictions.Maximum else
      amount


type Rules = {
  ChannelRules: Map<ShortChannelId, ThresholdRule>
  PeerRules: Map<NodeId, ThresholdRule>
}
  with
  static member Zero = {
    ChannelRules = Map.empty
    PeerRules = Map.empty
  }


[<AutoOpen>]
module Fees =
  type IFeeLimit =
    abstract member Validate: unit -> Result<unit, string>
    /// Checks whether we may dispatch swap based on the current fee conditions.
    /// (Only for loop out sweep tx)
    abstract member CheckWithEstimatedFee: feeRate: FeeRate -> Result<unit, SwapDisqualifiedReason>

    /// Checks whether the quote provided is within our fee limits for the swap amount.
    abstract member CheckLoopOutLimits: swapAmount: Money * quote: SwapDTO.LoopOutQuote -> Result<unit, SwapDisqualifiedReason>

    /// Returns the maximum amount of the loop-out specific fees,
    /// i.e. 1. prepay fee, 2. invoice routing fee for swap amount 3. miner fee for the sweep tx.
    abstract member LoopOutFees: amount: Money * quote: SwapDTO.LoopOutQuote -> Money * Money * Money
    abstract member CheckLoopInLimits: amount: Money * quote: SwapDTO.LoopInQuote -> Result<unit, SwapDisqualifiedReason>

  /// FeePortion is a fee limitation which limits fees to a set portion of the swap amount.
  type FeePortion = {
    PartsPerMillion: int64<ppm>
  }
    with
    static member Default = {
      // default percentage of swap amount that we allocate to fees, 2%.
      PartsPerMillion = defaultFeePPM
    }
    interface IFeeLimit with
      member this.Validate() =
        if this.PartsPerMillion |> int64 <= 0L then
          Error "Invalid Parts per million"
        else
          Ok()
      member this.CheckLoopOutLimits(swapAmount, quote) =
        // First, check whether any of the individual fee categories provided by the server are more than
        // our total limit. We do this so that we can provide more specific reasons for not executing swaps.
        let feeLimit = ppmToSat(swapAmount, this.PartsPerMillion)
        let minerFee = scaleMinerFee(quote.SweepMinerFee)
        if minerFee > feeLimit then
          Error <| SwapDisqualifiedReason.MinerFeeTooHigh({| ServerRequirement = minerFee; OurLimit = feeLimit |})
        elif quote.SwapFee > feeLimit then
          Error <| SwapDisqualifiedReason.SwapFeeTooHigh({| ServerRequirement = quote.SwapFee; OurLimit = feeLimit |})
        elif quote.PrepayAmount > feeLimit then
          Error <| SwapDisqualifiedReason.PrepayTooHigh({| ServerRequirement = quote.PrepayAmount; OurLimit = feeLimit |})
        elif minerFee + quote.SwapFee >= feeLimit then
          // if our miner and swap fee equal our limit, we will have nothing left for off-chain fees,
          // so we fail out early.
          Error <| SwapDisqualifiedReason.FeePPMInsufficient({| Required = minerFee + quote.SwapFee; OurLimit = feeLimit |})
        else
          let prepay, route, miner = (this :> IFeeLimit).LoopOutFees(swapAmount, quote)

          let fees = worstCaseOutFees({
              LoopOutLimits.MaxPrepayRoutingFee = prepay
              MaxPrepay = prepay
              MaxSwapFee = quote.SwapFee
              MaxRoutingFee = route
              MaxMinerFee = miner
              SwapTxConfRequirement = BlockHeightOffset32.Zero // unused dummy
            })
          if fees > feeLimit then
            Error <| SwapDisqualifiedReason.FeePPMInsufficient({| Required = fees; OurLimit = feeLimit |})
          else
            Ok()

      /// returns the maximum prepay and invoice routing fees for a swap amount and quote.
      /// Note that the fee portion implementation just returns the quote's miner fee, assuming
      /// that the quote's minerfee + swapfee < fee limit, so that we have some fees left for off-chain routing.
      member this.LoopOutFees(amount, quote) =
        let feeLimit = ppmToSat(amount, this.PartsPerMillion)
        let minerFee = scaleMinerFee(quote.SweepMinerFee)
        /// Takes
        /// 1. available total of the offchain fee,
        /// 2.
        let available = feeLimit - minerFee - quote.SwapFee
        let prepayMaxFee, routeMaxFee = splitOffChain(available, quote.PrepayAmount, amount)
        prepayMaxFee, routeMaxFee, minerFee

      member this.CheckLoopInLimits(amount, quote) =
        let feeLimit = ppmToSat(amount, this.PartsPerMillion)
        if quote.MinerFee >= feeLimit then
          Error <| SwapDisqualifiedReason.MinerFeeTooHigh({| ServerRequirement = quote.MinerFee; OurLimit = feeLimit |})
        elif quote.SwapFee > feeLimit then
          Error <| SwapDisqualifiedReason.SwapFeeTooHigh({| ServerRequirement = quote.SwapFee; OurLimit = feeLimit |})
        else
          let fees =
            worstCaseInFees
              {
                LoopInLimits.MaxMinerFee = quote.MinerFee
                MaxSwapFee = quote.SwapFee
              }
              defaultLoopInSweepFee
          if fees > feeLimit then
            Error <| SwapDisqualifiedReason.FeePPMInsufficient({| Required = fees; OurLimit = feeLimit |})
          else
            Ok()

      /// We do not do any checks for fee percentage, since we need full quote
      /// to determine whether we can perform a swap.
      member this.CheckWithEstimatedFee(_feeRate) =
        Ok()

type FeeCategoryLimit = {
  MaximumPrepay: Money
  MaximumSwapFeePPM: int64<ppm>
  MaximumRoutingFeePPM: int64<ppm>
  MaximumPrepayRoutingFeePPM: int64<ppm>
  MaximumMinerFee: Money
  SweepFeeRateLimit: FeeRate
}
  with
  interface IFeeLimit with
    member this.Validate(): Result<unit, string> =
      if this.MaximumSwapFeePPM <= 0L<ppm> then
        Error $"SwapFeePPM must be positive, it was {this.MaximumSwapFeePPM}"
      elif this.MaximumRoutingFeePPM <= 0L<ppm> then
        Error $"MaximumRoutingFeePPM must be positive, it was {this.MaximumRoutingFeePPM}"
      elif this.MaximumPrepayRoutingFeePPM <= 0L<ppm> then
        Error $"MaximumPrepayRoutingFeePPM must be positive, it was {this.MaximumPrepayRoutingFeePPM}"
      elif this.MaximumPrepay = Money.Zero then
        Error $"MaximumPrepay amount must be non-zero."
      elif this.MaximumMinerFee = Money.Zero then
        Error $"MaximumMinerFee amount must be non-zero."
      elif this.SweepFeeRateLimit = FeeRate.Zero then
        Error $"SweepFeeRateLimit must be non-zero."
      else
        Ok ()

    /// Checks whether we may dispatch swap based on the current fee conditions.
    /// (Only for loop out sweep tx)
    member this.CheckWithEstimatedFee(feeRate: FeeRate): Result<unit, SwapDisqualifiedReason> =
      feeRate.SatoshiPerByte > this.SweepFeeRateLimit.SatoshiPerByte
      |> Result.requireTrue(SwapDisqualifiedReason.SweepFeesTooHigh({| Estimation = feeRate; OurLimit = this.SweepFeeRateLimit |}))

    /// Checks whether the quote provided is within our fee limits for the swap amount.
    member this.CheckLoopOutLimits(amount: Money, quote: SwapDTO.LoopOutQuote): Result<unit, SwapDisqualifiedReason> =
      let maxFee = ppmToSat(amount, this.MaximumSwapFeePPM)
      if quote.SwapFee > maxFee then
        Error <| SwapDisqualifiedReason.SwapFeeTooHigh({| ServerRequirement = quote.SwapFee; OurLimit = maxFee |})
      elif quote.SweepMinerFee > this.MaximumMinerFee then
        Error <| SwapDisqualifiedReason.MinerFeeTooHigh({| ServerRequirement = quote.SweepMinerFee; OurLimit = this.MaximumMinerFee |})
      elif quote.PrepayAmount > this.MaximumPrepay then
        Error <| SwapDisqualifiedReason.PrepayTooHigh({| ServerRequirement = quote.PrepayAmount; OurLimit = this.MaximumPrepay |})
      else
        Ok ()

    member this.LoopOutFees(amount: Money, quote: SwapDTO.LoopOutQuote): Money * Money * Money =
      let prepayMaxFee = ppmToSat(quote.PrepayAmount, this.MaximumPrepayRoutingFeePPM)
      let routeMaxFee = ppmToSat(amount, this.MaximumRoutingFeePPM)
      prepayMaxFee, routeMaxFee, this.MaximumMinerFee

    member this.CheckLoopInLimits(amount: Money, quote: SwapDTO.LoopInQuote): Result<unit, SwapDisqualifiedReason> =
      let maxFee = ppmToSat(amount, this.MaximumSwapFeePPM)
      if quote.SwapFee > maxFee then
        Error <| SwapDisqualifiedReason.SwapFeeTooHigh({| ServerRequirement = quote.SwapFee; OurLimit = maxFee |})
      elif quote.MinerFee > this.MaximumMinerFee then
        Error <| SwapDisqualifiedReason.MinerFeeTooHigh({| ServerRequirement = quote.MinerFee; OurLimit = this.MaximumMinerFee |})
      else
        Ok()

/// run-time modifiable part of the auto loop parameters.
type Parameters = {
  AutoFeeBudget: Money
  AutoFeeStartDate: DateTimeOffset
  MaxAutoInFlight: int
  FailureBackoff: TimeSpan
  SweepConfTarget: BlockHeightOffset32
  HTLCConfTarget: BlockHeightOffset32
  FeeLimit: IFeeLimit
  ClientRestrictions: Restrictions option
  SwapTxConfRequirement: BlockHeightOffset32
  Rules: Rules
  AutoLoop: bool
}
  with
  static member Default(pairId: PairId) = {
    AutoFeeBudget = defaultBudget
    AutoFeeStartDate = DateTimeOffset.UtcNow
    MaxAutoInFlight = 1
    FailureBackoff = defaultFailureBackoff
    SweepConfTarget = pairId.DefaultLoopOutParameters.SweepConfTarget
    HTLCConfTarget = pairId.DefaultLoopInParameters.HTLCConfTarget
    FeeLimit = FeePortion.Default
    ClientRestrictions = None
    SwapTxConfRequirement = pairId.DefaultLoopOutParameters.SwapTxConfRequirement
    Rules = Rules.Zero
    AutoLoop = false
  }

  /// Checks whether a set of parameters is valid.
  member this.Validate(openChannels: ListChannelResponse seq, server): Result<unit, _> =
    result {
      // 1. validate rules for each peers and channels
      let channelsWithPeerRules =
        openChannels
        |> Seq.filter(fun c -> this.Rules.PeerRules |> Map.exists(fun k _ -> k.Value.Equals(c.NodeId)))
      for c in channelsWithPeerRules do
        if (this.Rules.ChannelRules |> Map.exists(fun k _ -> c.Id = k)) then
          return! Error $"Rules for peer: %s{c.NodeId.ToHex()} and its channel: %s{c.Id.AsString} can't both be set"
      for kv in this.Rules.ChannelRules do
        let channel, rule = kv.Key, kv.Value
        if (channel.ToUInt64() = 0UL) then
          return! Error("Channel has 0 channel id")
        do! rule.Validate() |> Result.mapError(fun m -> $"channel %s{channel.AsString} (%d{channel.ToUInt64()}) has invalid rule {m}")
      for kv in this.Rules.PeerRules do
        let peer, rule = kv.Key, kv.Value
        do! rule.Validate() |> Result.mapError(fun m -> $"peer %s{peer.Value.ToHex()} has invalid rule {m}")

      if (this.SweepConfTarget.Value < Constants.MinConfTarget) then
        return! Error $"confirmation target must be at least: %d{Constants.MinConfTarget}"

      do! this.FeeLimit.Validate()
      if this.AutoFeeBudget < Money.Zero then
        return! Error $"Negative Budget {this}"

      if this.MaxAutoInFlight <= 0 then
        return! Error $"Zero In Flight {this}"

      do!
        Restrictions.Validate(server, this.ClientRestrictions)
        |> Result.mapError(fun e -> e.Message)
      return ()
    }

  member this.HaveRules =
    this.Rules.ChannelRules.Count <> 0 || this.Rules.PeerRules.Count <> 0

type Config = {
  EstimateFee: IFeeEstimator
  SwapServerClient: ISwapServerClient
  Restrictions: Swap.Category -> Task<Result<Restrictions, string>>
  Lnd: INLoopLightningClient
  SwapActor: ISwapActor
}

type SuggestSwapError =
  | SwapDisqualified of SwapDisqualifiedReason
  | Other of string

type SwapTraffic = {
  OngoingLoopOut: list<ShortChannelId>
  OngoingLoopIn: list<NodeId>
  FailedLoopOut: Map<ShortChannelId, DateTimeOffset>
  FailedLoopIn: Map<NodeId, DateTimeOffset>
}

type TargetPeerOrChannel = {
  Peer: NodeId
  Channels: ShortChannelId array
}
type SwapBuilder = {
  /// Validate our swap is able to execute according to the current state of fee market.
  MaySwap: Parameters -> Task<Result<unit, SwapDisqualifiedReason>>
  /// Examines our current swap traffic to determine whether we should suggest the builder's type of swap for the peer
  /// and channels suggested.
  VerifyTargetIsNotInUse: SwapTraffic -> TargetPeerOrChannel -> Result<unit, SwapDisqualifiedReason>
  /// BuildSwap creates a swap for the target peer/channels provided. The autoloop boolean indicates whether this swap
  /// will actually be executed, because there are some calls we can leave out if this swap is just for a dry run.
  /// e.g. in loop out we don't have to bother getting a new on-chain address.
  BuildSwap: TargetPeerOrChannel -> Money -> INLoopLightningClient -> PairId -> bool -> Parameters -> Task<Result<SwapSuggestion, SwapDisqualifiedReason>>
}
  with
  static member NewLoopOut(cfg: Config, pairId: PairId, logger: ILogger): SwapBuilder =
    {
      MaySwap = fun parameters -> task {
        let! feeRate = cfg.EstimateFee.Estimate(parameters.SweepConfTarget) pairId.Base
        return parameters.FeeLimit.CheckWithEstimatedFee(feeRate)
      }
      VerifyTargetIsNotInUse = fun traffic { Peer = peer; Channels = channels } -> result {
        for chanId in channels do
          match traffic.FailedLoopOut.TryGetValue(chanId) with
          | true, lastFailedSwap ->
            // there is a recently failed swap.
            logger.LogDebug($"channel: {chanId} ({chanId.ToUInt64()}) not eligible for suggestions.
                                It was a part of the failed swap at: {lastFailedSwap}")
            return! Error(SwapDisqualifiedReason.FailureBackoff)
          | false, _ -> ()
          if traffic.OngoingLoopOut |> Seq.contains chanId then
            logger.LogDebug($"Channel: {chanId} ({chanId.ToUInt64()}) not eligible for suggestions.
                                Ongoing loop out utilizing channel.")
            return! Error(SwapDisqualifiedReason.LoopOutAlreadyInTheChannel)

        if traffic.OngoingLoopIn |> Seq.contains peer then
          return! Error(SwapDisqualifiedReason.LoopInAlreadyInTheChannel)

      }
      BuildSwap = fun { Peer = peer; Channels = channels } amount lnClient pairId autoloop parameters -> taskResult {
        let! quote =
          let req =
            { SwapDTO.LoopOutQuoteRequest.Pair = pairId
              SwapDTO.Amount = amount
              SwapDTO.SweepConfTarget = parameters.SweepConfTarget }
          cfg.SwapServerClient.GetLoopOutQuote(req)
        do! parameters.FeeLimit.CheckLoopOutLimits(amount, quote)
        let prepayMaxFee, routeMaxFee, minerMaxFee = parameters.FeeLimit.LoopOutFees(amount, quote)
        let! addr =
            if autoloop then
              cfg.Lnd.GetDepositAddress()
              |> Task.map Some
            else
              Task.FromResult None
        let req = {
          LoopOutRequest.Address = addr
          OutgoingChannelIds = channels
          PairId = pairId |> Some
          Amount = amount
          SwapTxConfRequirement =
            parameters.SwapTxConfRequirement.Value
            |> int |> Some
          Label =
            if autoloop then
              Labels.autoLoopLabel(Swap.Category.Out) |> Some
            else
              None
          MaxSwapRoutingFee = routeMaxFee |> ValueSome
          MaxPrepayRoutingFee = prepayMaxFee |> ValueSome
          MaxSwapFee = quote.SwapFee |> ValueSome
          MaxPrepayAmount = quote.PrepayAmount |> ValueSome
          MaxMinerFee = minerMaxFee |> ValueSome
          SweepConfTarget = parameters.SweepConfTarget.Value |> int |> ValueSome
        }
        return SwapSuggestion.Out(req)
      }
    }

  static member NewLoopIn(cfg: Config, logger: ILogger) =
    {
      VerifyTargetIsNotInUse = fun (traffic: SwapTraffic) ({ Channels = channels; Peer = peer }: TargetPeerOrChannel) -> result {
        for chanId in channels do
          if traffic.FailedLoopOut |> Map.containsKey(chanId) then
            logger.LogDebug($"Channel: {chanId} ({chanId.ToUInt64()}) not eligible for suggestions, ongoing loop out utilizing channel.")
            return! (Error(SwapDisqualifiedReason.LoopOutAlreadyInTheChannel))

        if traffic.OngoingLoopIn |> Seq.contains(peer) then
          logger.LogDebug($"Peer: {peer.Value.ToHex()} not eligible for suggestions ongoing, loopin utilizing peer")
          return! Error(SwapDisqualifiedReason.LoopInAlreadyInTheChannel)

        match traffic.FailedLoopIn.TryGetValue peer with
        | true, lastFailDate ->
          logger.LogDebug($"Peer: {peer.Value.ToHex()} not eligible for suggestions, There was failed swap at {lastFailDate}")
          return! Error(SwapDisqualifiedReason.FailureBackoff)
        | _ -> ()
      }
      /// For loop in, we cannot check any upfront costs because we do not know how many inputs will be used for our
      /// on-chain htlc before it is made, so we can't make any estimation.
      MaySwap = fun _ -> Task.FromResult(Ok())
      BuildSwap = fun { Channels = channels; Peer = peer } amount lnClient pairId autoloop parameters -> taskResult {
        let! quote =
          cfg.SwapServerClient.GetLoopInQuote({
            Amount = amount
            Pair = pairId
          })
        do! parameters.FeeLimit.CheckLoopInLimits(amount, quote)
        let! routeHints =
          channels
          |> Seq.map(chanIdToRouteHint lnClient)
          |> Task.WhenAll
        let req = {
          LoopInRequest.Amount = amount
          ChannelId = None
          Label = if autoloop then Labels.autoLoopLabel(Swap.Category.In) |> Some else None
          PairId = Some pairId
          MaxMinerFee = quote.MinerFee |> ValueSome
          MaxSwapFee = quote.SwapFee |> ValueSome
          HtlcConfTarget = parameters.HTLCConfTarget.Value |> int |> ValueSome
          RouteHints = routeHints
        }
        return
          SwapSuggestion.In(req)
      }
    }

type AutoLoopManager(logger: ILogger<AutoLoopManager>,
                     opts: IOptions<NLoopOptions>,
                     swapStateProjection: ISwapStateProjection,
                     recentSwapFailureProjection: IRecentSwapFailureProjection,
                     swapServerClient: ISwapServerClient,
                     blockChainListener: IBlockChainListener,
                     swapActor: ISwapActor,
                     feeEstimator: IFeeEstimator,
                     systemClock: ISystemClock,
                     _lightningClientProvider: ILightningClientProvider) =

  inherit BackgroundService()

  let mutable parametersDict =
    Map.empty<Swap.Group, Parameters>
  let _lockObj = obj()

  member this.Parameters
    with get () = parametersDict
    and private set (v: Map<Swap.Group, Parameters>) =
      lock _lockObj (fun () -> parametersDict <- v)

  member this.Config (g: Swap.Group) =
    {
      Config.Restrictions = fun category -> task {
        match category with
        | Swap.Category.Out ->
          try
            let! terms = swapServerClient.GetLoopOutTerms(g.PairId)
            return Ok { Restrictions.Maximum = terms.MaxSwapAmount
                        Minimum = terms.MinSwapAmount }
          with
          | ex ->
            return Error(ex.ToString())
        | Swap.Category.In ->
          try
            let! terms = swapServerClient.GetLoopInTerms(g.PairId)
            return Ok { Restrictions.Maximum = terms.MaxSwapAmount
                        Minimum = terms.MinSwapAmount }
          with
          | ex ->
            return Error(ex.ToString())
      }
      EstimateFee = feeEstimator
      SwapServerClient = swapServerClient
      Lnd = _lightningClientProvider.GetClient g.OffChainAsset
      SwapActor = swapActor
    }

  member this.Builder g =
    let c =
      this.Config g
    match g.Category with
    | Swap.Category.Out ->
      SwapBuilder.NewLoopOut(c, g.PairId, logger)
    | Swap.Category.In ->
      SwapBuilder.NewLoopIn(c, logger)

  member this.LightningClient (g: Swap.Group) =
    g.OffChainAsset
    |> _lightningClientProvider.GetClient

  member this.SetParameters(g: Swap.Group, v: Parameters) = taskResult {
    let! channels =
      _lightningClientProvider
        .GetClient(g.OffChainAsset)
        .ListChannels()
    let c = this.Config g
    let! r =
      c.Restrictions g.Category
    do! v.Validate(channels, r)
    this.Parameters <-
      this.Parameters |> Map.add g v
  }

  /// Query the server for its latest swap size restrictions,
  /// validates client restrictions (if present) against these values and merges the client's custom
  /// requirements with the server's limits to produce a single set of limitations for our swap.
  member private this.GetSwapRestrictions(group: Swap.Group): Task<Result<_, AutoLoopError>> = taskResult {
      let! restrictions = swapServerClient.GetSwapAmountRestrictions(group)
      let par = this.Parameters.[group]
      do!
          Restrictions.Validate(
            restrictions,
            par.ClientRestrictions
          )
          |> Result.mapError(AutoLoopError.RestrictionError)
      return
        match par.ClientRestrictions with
        | Some cr ->
          {
            Minimum =
              Money.Max(cr.Minimum, restrictions.Minimum)
            Maximum =
              if cr.Maximum <> Money.Zero && cr.Maximum < restrictions.Maximum then
                cr.Maximum
              else
                restrictions.Maximum
          }
        | None ->
          {
            Minimum = restrictions.Minimum
            Maximum = restrictions.Maximum
          }
    }

  member private this.SingleReasonSuggestion(group: Swap.Group, reason: SwapDisqualifiedReason): SwapSuggestions =
    let r = this.Parameters.[group].Rules
    { SwapSuggestions.Zero
        with
        DisqualifiedChannels = r.ChannelRules |> Map.map(fun _ _ -> reason)
        DisqualifiedPeers = r.PeerRules |> Map.map(fun _ _ -> reason)
    }

  member private this.CheckAutoLoopIsPossible(pairId, existingAutoLoopOuts: _ list, summary: ExistingAutoLoopSummary) =
    let par = this.Parameters.[pairId]
    let checkInFlightNumber () =
      if par.MaxAutoInFlight < existingAutoLoopOuts.Length then
        logger.LogDebug($"%d{par.MaxAutoInFlight} autoloops allowed, %d{existingAutoLoopOuts.Length} inflight")
        Error (this.SingleReasonSuggestion(pairId, SwapDisqualifiedReason.InFlightLimitReached))
      else
        Ok()
    let checkDate() =
      if par.AutoFeeStartDate > systemClock.UtcNow then
        Error <| this.SingleReasonSuggestion(pairId, SwapDisqualifiedReason.BudgetNotStarted)
      else
        Ok()
    result {
      do! checkDate()
      do! checkInFlightNumber()
      do! summary.ValidateAgainstBudget(par.AutoFeeBudget, logger)
          |> Result.mapError(fun r -> this.SingleReasonSuggestion(pairId, r))
    }

  member this.SuggestSwaps(autoloop: bool, group: Swap.Group, ?ct: CancellationToken): Task<Result<SwapSuggestions, AutoLoopError>> = task {
    let ct = defaultArg ct CancellationToken.None
    let builder = this.Builder group
    let par = this.Parameters.[group]
    if not <| par.HaveRules then return Error(AutoLoopError.NoRules) else
    match! builder.MaySwap(par) with
    | Error e ->
      return this.SingleReasonSuggestion(group, e) |> Ok
    | Ok() ->
      match! this.GetSwapRestrictions(group) with
      | Error e -> return Error e
      | Ok restrictions ->
      let onGoingLoopOuts: _ list = swapStateProjection.OngoingLoopOuts |> Seq.toList
      let onGoingLoopIns: _ list = swapStateProjection.OngoingLoopIns |> Seq.toList
      let summary =
        ExistingAutoLoopSummary.FromLoopInOuts(onGoingLoopIns, onGoingLoopOuts)
      let existingAutoLoopOuts =
        onGoingLoopOuts
        |> Seq.toList
      match this.CheckAutoLoopIsPossible(group, existingAutoLoopOuts, summary) with
      | Error e ->
        return e |> Ok
      | Ok () ->
        let! channels =
          (this.LightningClient group)
            .ListChannels(ct)
        let peerToChannelBalance: Map<NodeId, Balances> =
          channels
          |> List.map(Balances.FromLndResponse)
          |> List.groupBy(fun b -> b.PubKey)
          |> List.map(fun (nodeId, balances) -> nodeId, balances |> List.reduce(+))
          |> Map.ofList
        let chanToPeers: Map<ShortChannelId, NodeId> =
          channels
          |> List.map(fun c -> c.Id, c.NodeId |> NodeId)
          |> Map.ofList
        let peersWithRules =
          peerToChannelBalance
          |> Map.toSeq
          |> Seq.choose(fun (nodeId, balance) ->
            par.Rules.PeerRules
            |> Map.tryFind(nodeId)
            |> Option.map(fun rule -> (nodeId, balance, rule))
          )

        let mutable resp = SwapSuggestions.Zero
        let mutable suggestions: ResizeArray<SwapSuggestion> = ResizeArray()
        let traffic = {
          SwapTraffic.FailedLoopOut =
            recentSwapFailureProjection.FailedLoopOuts
            |> Map.filter(fun _ v -> (systemClock.UtcNow - par.FailureBackoff) <= v)
          FailedLoopIn =
            recentSwapFailureProjection.FailedLoopIns
            |> Map.filter(fun _ v -> (systemClock.UtcNow - par.FailureBackoff) <= v)
          OngoingLoopOut =
            onGoingLoopOuts
            |> List.map(fun o -> o.OutgoingChanIds |> Array.toList) |> List.concat
          OngoingLoopIn =
            onGoingLoopIns
            |> List.map(fun i -> i.LastHop |> Option.map(NodeId) |> Option.toList)
            |> List.concat
        }

        for nodeId, balances, rule in peersWithRules do
          match! this.SuggestSwap(traffic, balances, rule, restrictions, group,  autoloop) with
          | Error e ->
            resp <- { resp with DisqualifiedPeers = resp.DisqualifiedPeers |> Map.add nodeId e }
          | Ok (SwapSuggestion.In s) ->
            // Create a route_hint for every channel against the last_hop
            // to tell them which channel we want the inbound liquidity for.
            let targetChannels =
              channels
              |> List.filter(fun c -> match s.LastHop with | None -> false | Some lastHop -> lastHop = c.NodeId)
            let! routeHints =
              targetChannels
              |> Seq.map(fun r -> r.Id |> chanIdToRouteHint (this.LightningClient group))
              |> Task.WhenAll
            let s =
              { s with RouteHints = routeHints }
            suggestions.Add (SwapSuggestion.In s)
          | Ok s ->
            suggestions.Add(s)

        for c in channels do
          let balance = Balances.FromLndResponse c
          match par.Rules.ChannelRules.TryGetValue c.Id with
          | false, _ -> ()
          | true, rule ->
            match! this.SuggestSwap(traffic, balance, rule, restrictions, group, autoloop) with
            | Error e ->
              resp <- { resp with DisqualifiedChannels = resp.DisqualifiedChannels |> Map.add c.Id e }
            | Ok s ->
              suggestions.Add s

        if suggestions.Count = 0 then
          return Ok resp
        else
          let suggestions = suggestions |> Seq.sortBy(fun s -> s.Amount)

          let setReason
            (reason: SwapDisqualifiedReason)
            (swapSuggestion: SwapSuggestion)
            (response: SwapSuggestions) =
            {
              response
                with
                DisqualifiedPeers =
                  swapSuggestion.Peers(chanToPeers, logger)
                  |> Seq.filter(fun p -> par.Rules.PeerRules |> Map.containsKey p)
                  |> Seq.fold(fun acc p ->
                    acc |> Map.add p reason
                  ) response.DisqualifiedPeers
                DisqualifiedChannels =
                  swapSuggestion.Channels
                  |> Seq.filter(fun c -> par.Rules.ChannelRules |> Map.containsKey c)
                  |> Seq.fold(fun acc c ->
                    acc |> Map.add c reason
                  ) response.DisqualifiedChannels
            }

          // Run through our suggested swaps  in descending order of amount and return all of the swaps which will
          // fit within our remaining budget.
          let mutable available = par.AutoFeeBudget - summary.TotalFees
          let allowedSwaps = par.MaxAutoInFlight - existingAutoLoopOuts.Count()
          let resp, _ =
            suggestions
            |> Seq.fold(fun (acc, available) s ->
              let fees = s.Fees()
              let subtractFee a =
                if (fees <= a) then
                  a - fees
                else
                  a
              if available = Money.Zero then
                (setReason SwapDisqualifiedReason.BudgetInsufficient s acc), available
              elif resp.OutSwaps.Length = allowedSwaps || resp.InSwaps.Length = allowedSwaps then
                (setReason SwapDisqualifiedReason.InFlightLimitReached s acc), available
              else
                acc.AddSuggestion(s), (subtractFee available)
            ) (resp, available)

          return Ok resp
  }

  member private this.SuggestSwap(traffic, balance: Balances, rule: ThresholdRule, restrictions: Restrictions, group, autoloop: bool): Task<Result<SwapSuggestion, SwapDisqualifiedReason>> =
    taskResult {
      let builder = this.Builder group
      let peerOrChannel = { Peer = balance.PubKey; Channels = balance.Channels.ToArray() }
      do! builder.VerifyTargetIsNotInUse(traffic) (peerOrChannel)

      let amount = rule.SwapAmount(balance, restrictions, opts.Value.TargetIncomingLiquidityRatio)
      if amount = Money.Zero then
        return! Error(SwapDisqualifiedReason.LiquidityOk)
      else
        let par = this.Parameters.[group]
        return!
          builder.BuildSwap peerOrChannel amount (this.LightningClient group) group.PairId autoloop par
    }

  /// Gets a set of suggested swaps and dispatches them automatically if we have automated looping enabled.
  member this.AutoLoop(group, ct): Task<Result<_, AutoLoopError>> = taskResult {
    let! suggestion = this.SuggestSwaps(true, group, ct)

    for swap in suggestion.OutSwaps do
      let par = this.Parameters.[group]
      if not <| par.AutoLoop then
        let chanSet = swap.OutgoingChannelIds |> Array.fold(fun acc c -> $"{acc},{c} ({c.ToUInt64()})") ""
        logger.LogDebug($"recommended autoloop out: {swap.Amount.Satoshi} sats over {chanSet}")
      else
        let! loopOut =
          swapActor.ExecNewLoopOut(swap, blockChainListener.CurrentHeight)
          |> TaskResult.mapError(AutoLoopError.FailedToDispatchLoop)
        logger.LogInformation($"loop out automatically dispatched.: (id {loopOut.Id}, onchain address: {loopOut.ClaimAddress}. amount: {loopOut.OnChainAmount.Satoshi} sats)")

    for inSwap in suggestion.InSwaps do
      let par = this.Parameters.[group]
      if not <| par.AutoLoop then
        logger.LogDebug($"recommended autoloop in: %d{inSwap.Amount.Satoshi} sats over {inSwap.ChannelId} ({inSwap.ChannelId |> Option.map(fun c -> c.ToUInt64())})")
      else
        let! loopIn =
          swapActor.ExecNewLoopIn(inSwap, blockChainListener.CurrentHeight)
          |> TaskResult.mapError(AutoLoopError.FailedToDispatchLoop)
        logger.LogInformation($"loop in automatically dispatched: (id: {loopIn.Id})")

    return ()
  }

  override this.ExecuteAsync(stoppingToken) = unitTask {
    try
      while not <| stoppingToken.IsCancellationRequested do
        for group, _ in this.Parameters |> Map.toSeq do
          match! this.AutoLoop(group, stoppingToken) with
          | Ok() -> ()
          | Error e ->
            logger.LogError($"Error in autoloop ({PairId.toStringFromVal(group.PairId)}): {e}")
        do! Task.Delay tick
    with
    | :? OperationCanceledException ->
      logger.LogInformation($"Stopping {nameof(AutoLoopManager)}...")
    | ex ->
      logger.LogError($"{ex}")
  }
