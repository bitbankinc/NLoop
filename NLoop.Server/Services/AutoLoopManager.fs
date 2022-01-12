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
open NLoop.Server.Options
open NLoop.Server.DTOs
open NLoop.Server.Projections
open NLoop.Server.RPCDTOs
open NLoop.Server.SwapServerClient

[<AutoOpen>]
module internal AutoLoopConstants =
  /// We use static fee rate to estimate our sweep fee, because we can't realistically
  /// estimate what our fee estimate will be by the time we reach timeout. We set this to a
  /// high estimate so that we can account for worst-case fees, (1250 * 4 / 1000) = 5 sat/byte
  let defaultLoopInSweepFee = FeeRate(5m)

  let [<Literal>] defaultFeePPM =
    20000L<ppm>
  /// default number of the upper limit of the on-going swap number.
  let defaultMaxInFlight = 1

  let defaultFailureBackoff = TimeSpan.FromHours(24.)

  let [<Literal>] defaultMaxRoutingFeePPM = 10000L<ppm>
  let [<Literal>] defaultMaxPrepayRoutingFeePPM = 5000L<ppm>

  let tick = TimeSpan.FromSeconds(20.)

[<AutoOpen>]
module internal AutoLoopHelpers =

  let splitOffChain(available: Money, prepayAmount: Money, swapAmount: Money) =
    let total = swapAmount + prepayAmount
    let prepayMaxFee = ((available.Satoshi * prepayAmount.Satoshi) / total.Satoshi) |> Money.Satoshis
    let routeMaxFee = ((available.Satoshi * swapAmount.Satoshi) / total.Satoshi) |> Money.Satoshis
    prepayMaxFee, routeMaxFee

  let scaleMinerFee (fee: Money) =
    100 * fee


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
  | ZeroInFlight
  | NoRules
  | RestrictionError of RestrictionError
  | ExclusiveRules
  | FailedToDispatchLoop of msg: string
  | FailedToGetServerRestriction of exn
  | InvalidParameters of string
  with
  member this.Message =
    match this with
    | ZeroInFlight -> "max in flight swap must be >= 0"
    | NoRules -> "No rules set for autoloop"
    | RestrictionError r -> r.Message
    | ExclusiveRules ->
      $"channel and peer rules must be exclusive"
    | FailedToDispatchLoop msg -> msg
    | InvalidParameters msg -> msg
    | e -> e.ToString()

[<RequireQualifiedAccess>]
type SwapSuggestion =
  | Out of LoopOutRequest
  | In of LoopInRequest
  with
  member this.Fees(): Money =
    match this with
    | Out req ->
      req.Limits.WorstCaseFee
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

  static member (+) (a: SwapSuggestions, b: SwapSuggestions) =
    {
      DisqualifiedChannels =
        // a channel is disqualified iff it is disqualified both a and b
        a.DisqualifiedChannels |> Map.filter(fun k v -> Map.containsKey k b.DisqualifiedChannels)
      DisqualifiedPeers =
        // a peer is disqualified iff it is disqualified both a and b
        a.DisqualifiedPeers |> Map.filter(fun k v -> Map.containsKey k b.DisqualifiedPeers)
      OutSwaps = a.OutSwaps @ b.OutSwaps
      InSwaps = a.InSwaps @ b.InSwaps
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

type SwapRestrictions = ServerRestrictions

[<AutoOpen>]
module private Extensions =

  type ThresholdRule with
    /// SwapAmount suggests a swap based on the liquidity thresholds configured,
    /// returning zero if no swap is recommended.
    member this.SwapAmount(channelBalances: Balances, outRestrictions: ServerRestrictions, targetIncomingLiquidityRatio: int16<percent>): Money =
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
  static member FromDTOs(dtos: LiquidityRule[]) = {
    ChannelRules =
      dtos
      |> Array.map(fun dto -> (dto.ChannelId, { ThresholdRule.MinimumIncoming = dto.IncomingThreshold
                                                MinimumOutGoing = dto.OutgoingThreshold }))
      |> Map.ofArray
    PeerRules =
      dtos
      |> Array.map(fun dto -> (dto.PubKey |> NodeId, { ThresholdRule.MinimumIncoming = dto.IncomingThreshold
                                                       MinimumOutGoing = dto.OutgoingThreshold }))
      |> Map.ofArray
  }

  static member Zero = {
    ChannelRules = Map.empty
    PeerRules = Map.empty
  }


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

        let fees =
          let limits = {
            LoopOutLimits.MaxPrepayRoutingFee = prepay
            MaxPrepay = prepay
            MaxSwapFee = quote.SwapFee
            MaxRoutingFee = route
            MaxMinerFee = miner
            SwapTxConfRequirement = BlockHeightOffset32.Zero // unused dummy
            MaxCLTVDelta = BlockHeightOffset32.Zero // unused dummy
          }
          limits.WorstCaseFee
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
  static member Default(offChainP: CryptoCodeDefaultOffChainParams, onChainP: CryptoCodeDefaultOnChainParams) =
    {
      MaximumPrepay = offChainP.MaxPrepay
      MaximumSwapFeePPM = offChainP.MaxSwapFeePPM
      MaximumRoutingFeePPM = defaultMaxRoutingFeePPM
      MaximumPrepayRoutingFeePPM = defaultMaxPrepayRoutingFeePPM
      MaximumMinerFee = onChainP.MaxMinerFee
      SweepFeeRateLimit = onChainP.SweepFeeRateLimit
    }
  interface IFeeLimit with
    member this.Validate(): Result<unit, string> =
      if this.MaximumSwapFeePPM <= 0L<ppm> then
        Error $"{nameof(this.MaximumSwapFeePPM)} must be positive, it was ({this.MaximumSwapFeePPM})"
      elif this.MaximumRoutingFeePPM <= 0L<ppm> then
        Error $"{nameof(this.MaximumRoutingFeePPM)} must be positive, it was {this.MaximumRoutingFeePPM}"
      elif this.MaximumPrepayRoutingFeePPM <= 0L<ppm> then
        Error $"{nameof(this.MaximumPrepayRoutingFeePPM)} must be positive, it was {this.MaximumPrepayRoutingFeePPM}"
      elif this.MaximumPrepay = Money.Zero then
        Error $"{nameof(this.MaximumPrepay)} amount must be non-zero."
      elif this.MaximumMinerFee = Money.Zero then
        Error $"{nameof(this.MaximumMinerFee)} amount must be non-zero."
      elif this.SweepFeeRateLimit = FeeRate.Zero then
        Error $"{nameof(this.SweepFeeRateLimit)} must be non-zero."
      else
        Ok ()

    /// Checks whether we may dispatch swap based on the current fee conditions.
    /// (Only for loop out sweep tx)
    member this.CheckWithEstimatedFee(feeRate: FeeRate): Result<unit, SwapDisqualifiedReason> =
      feeRate.SatoshiPerByte <= this.SweepFeeRateLimit.SatoshiPerByte
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
  /// maximum number of in-flight automatically dispatched swaps we allow.
  MaxAutoInFlight: int
  FailureBackoff: TimeSpan
  /// We use this number when estimating the fee for sweep tx in loop-out.
  SweepConfTarget: BlockHeightOffset32
  FeeLimit: IFeeLimit
  /// restriction placed on swap size by the client.
  ClientRestrictions: ClientRestrictions
  Rules: Rules
  /// We use this number when estimating the fee for swap tx in loop-in.
  HTLCConfTarget: BlockHeightOffset32
  /// if we dispatch the actual swap or not.
  AutoLoop: bool
  /// On-chain asset we use for the swap.
  OnChainAsset: SupportedCryptoCode
}
  with
  static member Default(onChain: SupportedCryptoCode) =
    {
      MaxAutoInFlight = 1
      FailureBackoff = defaultFailureBackoff
      SweepConfTarget = onChain.DefaultParams.OnChain.SweepConfTarget
      FeeLimit = FeePortion.Default
      ClientRestrictions = ClientRestrictions.Default
      Rules = Rules.Zero
      HTLCConfTarget = onChain.DefaultParams.OnChain.HTLCConfTarget
      AutoLoop = false
      OnChainAsset = onChain
    }

  /// Checks whether a set of parameters is valid.
  member this.Validate(openChannels: ListChannelResponse seq,
                       loopInServerRestrictions: ServerRestrictions,
                       loopOutServerRestrictions: ServerRestrictions): Result<unit, string> =
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

      if this.MaxAutoInFlight <= 0 then
        return! Error $"Zero In Flight {this}"

      do!
        loopInServerRestrictions.Validate(this.ClientRestrictions, Swap.Category.In)
        |> Result.mapError(fun e -> e.Message)
      do!
        loopOutServerRestrictions.Validate(this.ClientRestrictions, Swap.Category.Out)
        |> Result.mapError(fun e -> e.Message)
      return ()
    }

  member this.HaveRules =
    this.Rules.ChannelRules.Count <> 0 || this.Rules.PeerRules.Count <> 0

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
type LoopOutSwapBuilderDeps = {
  FeeEstimator: IFeeEstimator
  GetLoopOutQuote: SwapDTO.LoopOutQuoteRequest -> Task<Result<SwapDTO.LoopOutQuote, exn>>
  GetDepositAddress: GetAddress
}
type LoopInSwapBuilderDeps = {
  GetLoopInQuote: SwapDTO.LoopInQuoteRequest -> Task<Result<SwapDTO.LoopInQuote, exn>>
}

type Config = {
  EstimateFee: IFeeEstimator
  SwapServerClient: ISwapServerClient
  Restrictions: Swap.Category -> Task<Result<ServerRestrictions, exn>>
  GetDepositAddress: GetAddress
  SwapExecutor: ISwapExecutor
}
  with
    member this.GetLoopOutSwapBuilderDeps() =
      {
        LoopOutSwapBuilderDeps.FeeEstimator = this.EstimateFee
        GetLoopOutQuote = fun req -> task {
          try
            let! resp =
              this.SwapServerClient.GetLoopOutQuote req
            return resp |> Ok
          with
          | ex -> return Error ex
        }
        GetDepositAddress = this.GetDepositAddress
      }
    member this.GetLoopInSwapBuilderDeps() = {
      LoopInSwapBuilderDeps.GetLoopInQuote =
        fun req -> task {
          try
            let! resp = this.SwapServerClient.GetLoopInQuote req
            return resp |> Ok
          with
          | ex -> return Error ex
        }
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
  BuildSwap: TargetPeerOrChannel -> Money -> PairId -> bool -> Parameters -> Task<Result<SwapSuggestion, SwapDisqualifiedReason>>
}
  with
  static member NewLoopOut(cfg: LoopOutSwapBuilderDeps, pairId: PairId, logger: ILogger): SwapBuilder =
    {
      MaySwap = fun parameters -> task {
        let! feeRate = cfg.FeeEstimator.Estimate(parameters.SweepConfTarget) pairId.Base
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
      BuildSwap = fun { Channels = channels } amount pairId autoloop parameters -> taskResult {
        let! quote =
          let req =
            { SwapDTO.LoopOutQuoteRequest.Pair = pairId
              SwapDTO.Amount = amount
              SwapDTO.SweepConfTarget = parameters.SweepConfTarget }
          cfg.GetLoopOutQuote(req)
          |> TaskResult.mapError(fun ex -> SwapDisqualifiedReason.LoopOutUnreachable(ex.Message))
        do! parameters.FeeLimit.CheckLoopOutLimits(amount, quote)
        let prepayMaxFee, routeMaxFee, minerMaxFee = parameters.FeeLimit.LoopOutFees(amount, quote)
        let! addr =
            if autoloop then
              cfg.GetDepositAddress.Invoke(pairId.Base)
              |> TaskResult.map Some
              |> TaskResult.mapError(SwapDisqualifiedReason.FailedToGetOnChainAddress)
            else
              TaskResult.retn None
        let req = {
          LoopOutRequest.Address = addr
          ChannelIds = channels |> ValueSome
          PairId = pairId |> Some
          Amount = amount
          SwapTxConfRequirement =
            pairId.DefaultLoopOutParameters.SwapTxConfRequirement.Value
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

  static member NewLoopIn(cfg: LoopInSwapBuilderDeps, logger: ILogger) =
    {
      VerifyTargetIsNotInUse = fun (traffic: SwapTraffic) ({ Channels = channels; Peer = peer }: TargetPeerOrChannel) -> result {
        for chanId in channels do
          if traffic.OngoingLoopOut |> Seq.contains(chanId) then
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
      BuildSwap =
        fun
          { Peer = peer; }
          amount
          pairId autoloop parameters -> taskResult {
        let! quote =
          cfg.GetLoopInQuote({
            Amount = amount
            Pair = pairId
          })
          |> TaskResult.mapError(fun ex -> SwapDisqualifiedReason.LoopInUnReachable(ex.Message))
        do! parameters.FeeLimit.CheckLoopInLimits(amount, quote)
        let req = {
          LoopInRequest.Amount = amount
          Label = if autoloop then Labels.autoLoopLabel(Swap.Category.In) |> Some else None
          PairId = Some pairId
          MaxMinerFee = quote.MinerFee |> ValueSome
          MaxSwapFee = quote.SwapFee |> ValueSome
          HtlcConfTarget = parameters.HTLCConfTarget.Value |> int |> ValueSome
          LastHop = peer.Value |> Some
          ChannelId = None
        }
        return
          SwapSuggestion.In(req)
      }
    }

type AutoLoopManager(logger: ILogger<AutoLoopManager>,
                     opts: IOptions<NLoopOptions>,
                     swapStateProjection: IOnGoingSwapStateProjection,
                     recentSwapFailureProjection: IRecentSwapFailureProjection,
                     swapServerClient: ISwapServerClient,
                     blockChainListener: IBlockChainListener,
                     swapActor: ISwapExecutor,
                     feeEstimator: IFeeEstimator,
                     systemClock: ISystemClock,
                     getAddress: GetAddress,
                     offChainAsset: SupportedCryptoCode,
                     _lightningClientProvider: ILightningClientProvider) =

  inherit BackgroundService()

  let lnClient = _lightningClientProvider.GetClient(offChainAsset)

  let mutable parameters = None
  let getPairId category =
    match category with
    | Swap.Category.Out ->
      PairId(parameters.Value.OnChainAsset, offChainAsset)
    | Swap.Category.In ->
      PairId(offChainAsset, parameters.Value.OnChainAsset)
  let getGroup category =
    {
      Swap.Group.Category = category
      Swap.Group.PairId = getPairId category
    }

  let _lockObj = obj()

  member this.Parameters
    with get () = parameters
    and private set (v: Parameters option) =
      lock _lockObj (fun () -> parameters <- v)

  member this.Config =
    this.GetConfig(getPairId)
  member this.GetConfig(getPairId: Swap.Category -> PairId) =
    {
      Config.Restrictions =
        fun category -> task {
          try
            let g = {
              Swap.Group.Category = category
              Swap.Group.PairId = getPairId category
            }
            return!
              swapServerClient.GetSwapAmountRestrictions(g, zeroConf=false)
              |> Task.map(Ok)
          with
          | ex ->
            return Error(ex)
      }
      EstimateFee = feeEstimator
      SwapServerClient = swapServerClient
      GetDepositAddress = getAddress
      SwapExecutor = swapActor
    }

  member this.Builder(category) =
    match category with
    | Swap.Category.Out ->
      let p = getPairId category
      SwapBuilder.NewLoopOut(this.Config.GetLoopOutSwapBuilderDeps(), p, logger)
    | Swap.Category.In ->
      SwapBuilder.NewLoopIn(this.Config.GetLoopInSwapBuilderDeps(), logger)

  member this.SetParameters(v: Parameters): Task<Result<_, AutoLoopError>> = taskResult {
    let! channels =
      lnClient
        .ListChannels()
    let getPairId category =
      match category with
      | Swap.Category.Out ->
        PairId(v.OnChainAsset, offChainAsset)
      | Swap.Category.In ->
        PairId(offChainAsset, v.OnChainAsset)
    let cfg = this.GetConfig getPairId
    let! restrictions =
      [Swap.Category.In; Swap.Category.Out]
      |> Seq.map(cfg.Restrictions >> TaskResult.mapError AutoLoopError.FailedToGetServerRestriction)
      |> Task.WhenAll
      |> Task.map (Seq.toList >> List.sequenceResultM)
    do!
      v.Validate(channels, restrictions.[0], restrictions.[1])
      |> Result.mapError(AutoLoopError.InvalidParameters)
    this.Parameters <- Some v
  }

  /// Query the server for its latest swap size restrictions,
  /// validates client restrictions (if present) against these values and merges the client's custom
  /// requirements with the server's limits to produce a single set of limitations for our swap.
  member private this.GetSwapRestrictions(category): Task<Result<_, AutoLoopError>> = taskResult {
      let! restrictions =
        let group = getGroup category
        swapServerClient.GetSwapAmountRestrictions(group, zeroConf=false)
      let par = this.Parameters.Value
      do!
        restrictions.Validate(par.ClientRestrictions, category)
        |> Result.mapError(AutoLoopError.RestrictionError)
      return
        match category with
        | Swap.Category.In ->
          {
            ServerRestrictions.Minimum =
              match par.ClientRestrictions.InMinimum with
              | Some min when min > restrictions.Minimum ->
                min
              | _ -> restrictions.Minimum
            Maximum =
              match par.ClientRestrictions.InMaximum with
              | Some max when max < restrictions.Maximum ->
                max
              | _ -> restrictions.Maximum
          }
        | Swap.Category.Out ->
          {
            ServerRestrictions.Minimum =
              match par.ClientRestrictions.OutMinimum with
              | Some min when min > restrictions.Minimum ->
                min
              | _ -> restrictions.Minimum
            Maximum =
              match par.ClientRestrictions.OutMaximum with
              | Some max when max < restrictions.Maximum ->
                max
              | _ -> restrictions.Maximum
          }
    }

  member private this.SingleReasonSuggestion(reason: SwapDisqualifiedReason): SwapSuggestions =
    let r = this.Parameters.Value.Rules
    { SwapSuggestions.Zero
        with
        DisqualifiedChannels = r.ChannelRules |> Map.map(fun _ _ -> reason)
        DisqualifiedPeers = r.PeerRules |> Map.map(fun _ _ -> reason)
    }

  member private this.CheckAutoLoopIsPossible(existingAutoLoopOuts: _ list, existingLoopIns: _ list) =
    let par = this.Parameters.Value
    let checkInFlightNumber () =
      if par.MaxAutoInFlight < existingAutoLoopOuts.Length + existingLoopIns.Length then
        logger.LogDebug($"%d{par.MaxAutoInFlight} autoloops allowed, %d{existingAutoLoopOuts.Length} inflight")
        Error (this.SingleReasonSuggestion SwapDisqualifiedReason.InFlightLimitReached)
      else
        Ok()
    result {
      do! checkInFlightNumber()
    }

  member this.SuggestLoopInOrOutSwaps(autoloop:bool, group: Swap.Group, onGoingLoopOuts: _ list, onGoingLoopIns: LoopIn list, restrictions: ServerRestrictions, ct: CancellationToken):Task<Result<SwapSuggestions, AutoLoopError>> =
    task {
      let par = this.Parameters.Value
      let! channels =
        lnClient
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
      let traffic =
        let failureCutoff = (systemClock.UtcNow - par.FailureBackoff)
        {
          SwapTraffic.FailedLoopOut =
            recentSwapFailureProjection.FailedLoopOuts
            |> Map.filter(fun _ v -> failureCutoff <= v)
          FailedLoopIn =
            recentSwapFailureProjection.FailedLoopIns
            |> Map.filter(fun _ v -> failureCutoff <= v)
          OngoingLoopOut =
            onGoingLoopOuts
            |> List.map(fun o -> o.OutgoingChanIds |> Array.toList) |> List.concat
          OngoingLoopIn =
            onGoingLoopIns
            |> List.map(fun i -> i.LastHop |> Option.map(NodeId) |> Option.toList)
            |> List.concat
        }

      for nodeId, balances, rule in peersWithRules do
        match! this.SuggestSwap(traffic, balances, rule, restrictions, group.Category, autoloop) with
        | Error e ->
          resp <- { resp with DisqualifiedPeers = resp.DisqualifiedPeers |> Map.add nodeId e }
        | Ok (SwapSuggestion.In s) ->
          suggestions.Add (SwapSuggestion.In s)
        | Ok s ->
          suggestions.Add(s)

      for c in channels do
        let balance = Balances.FromLndResponse c
        match par.Rules.ChannelRules.TryGetValue c.Id with
        | false, _ -> ()
        | true, rule ->
          match! this.SuggestSwap(traffic, balance, rule, restrictions, group.Category, autoloop) with
          | Error e ->
            resp <- { resp with DisqualifiedChannels = resp.DisqualifiedChannels |> Map.add c.Id e }
          | Ok s ->
            suggestions.Add s

      if suggestions.Count = 0 then
        return Ok resp
      else
        // reorder the suggestions so that we prioritize the large swap.
        let suggestions =
          suggestions
          |> Seq.sortByDescending(fun s -> (s.Amount, s.Channels))

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

        let allowedSwaps = par.MaxAutoInFlight - onGoingLoopOuts.Count()
        let resp =
          suggestions
          |> Seq.fold(fun (acc: SwapSuggestions) (s: SwapSuggestion) ->
            if acc.OutSwaps.Length >= allowedSwaps || acc.InSwaps.Length >= allowedSwaps then
              (setReason SwapDisqualifiedReason.InFlightLimitReached s acc)
            else
              acc.AddSuggestion(s)
          ) resp

        return Ok resp
    }

  member this.SuggestSwaps(autoloop: bool, ?ct: CancellationToken): Task<Result<SwapSuggestions, AutoLoopError>> = task {
    let ct = defaultArg ct CancellationToken.None
    if this.Parameters.IsNone || not <| this.Parameters.Value.HaveRules then return Error(AutoLoopError.NoRules) else
    let onGoingLoopOuts =
      swapStateProjection.OngoingLoopOuts |> Seq.toList
    let onGoingLoopIns =
      swapStateProjection.OngoingLoopIns |> Seq.toList
    match this.CheckAutoLoopIsPossible(onGoingLoopOuts, onGoingLoopIns) with
    | Error e ->
      return e |> Ok
    | Ok () ->
    let! suggestionResults =
      [Swap.Category.Out; Swap.Category.In]
      |> Seq.map(fun cat -> task {
          match! this.GetSwapRestrictions cat with
          | Error e -> return Error e
          | Ok restrictions ->
            let group = getGroup cat
            return! this.SuggestLoopInOrOutSwaps(autoloop, group, onGoingLoopOuts, onGoingLoopIns, restrictions, ct)
        }
      )
      |> Task.WhenAll
    match suggestionResults |> Seq.toList |> List.sequenceResultM with
    | Ok suggestions ->
      return suggestions |> Seq.reduce(+) |> Ok
    | Error e -> return Error e
  }

  member private this.SuggestSwap(traffic, balance: Balances, rule: ThresholdRule, restrictions: ServerRestrictions, category: Swap.Category, autoloop: bool): Task<Result<SwapSuggestion, SwapDisqualifiedReason>> =
    taskResult {
      let builder =
        this.Builder category
      let par = this.Parameters.Value
      do! builder.MaySwap(par)
      let peerOrChannel = { Peer = balance.PubKey; Channels = balance.Channels.ToArray() }
      do! builder.VerifyTargetIsNotInUse traffic peerOrChannel

      let amount = rule.SwapAmount(balance, restrictions, opts.Value.TargetIncomingLiquidityRatio)
      if amount = Money.Zero then
        return! Error(SwapDisqualifiedReason.LiquidityOk)
      else
        return!
          builder.BuildSwap
            peerOrChannel
            amount
            (getPairId category)
            autoloop
            par
    }

  /// Gets a set of suggested swaps and dispatches them automatically if we have automated looping enabled.
  member this.AutoLoop ct: Task<Result<_, AutoLoopError>> = taskResult {
    let! suggestion = this.SuggestSwaps(true, ct)

    let par = this.Parameters.Value
    for swap in suggestion.OutSwaps do
      if not <| par.AutoLoop then
        let chanSet = swap.OutgoingChannelIds |> Array.fold(fun acc c -> $"{acc},{c} ({c.ToUInt64()})") ""
        logger.LogDebug($"recommended autoloop out: {swap.Amount.Satoshi} sats over {chanSet}")
      else
        let group = getGroup Swap.Category.Out
        let! loopOut =
          swapActor.ExecNewLoopOut(swap, blockChainListener.CurrentHeight(group.OnChainAsset), nameof(AutoLoopManager), ct)
          |> TaskResult.mapError(AutoLoopError.FailedToDispatchLoop)
        logger.LogInformation($"loop out automatically dispatched.: (id {loopOut.Id}, onchain address: {loopOut.Address}.")

    for inSwap in suggestion.InSwaps do
      if not <| par.AutoLoop then
        logger.LogDebug($"recommended autoloop in: %d{inSwap.Amount.Satoshi} sats over {inSwap.LastHop} ({inSwap.ChannelId |> Option.map(fun c -> c.ToUInt64())})")
      else
        let group = getGroup Swap.Category.In
        let! loopIn =
          swapActor.ExecNewLoopIn(inSwap, blockChainListener.CurrentHeight(group.OnChainAsset), nameof(AutoLoopManager), ct)
          |> TaskResult.mapError(AutoLoopError.FailedToDispatchLoop)
        logger.LogInformation($"loop in automatically dispatched: (id: {loopIn.Id})")
    return ()
  }

  member internal this.RunStep(ct) = unitTask {
    match this.Parameters with
    | Some p ->
      match! this.AutoLoop(ct) with
      | Ok() -> ()
      | Error e ->
        logger.LogError($"Error in autoloop (OnChain: {p.OnChainAsset}, offChain: {offChainAsset}): {e}")
    | None -> ()
  }

  override this.ExecuteAsync(stoppingToken) = unitTask {
    try
      while not <| stoppingToken.IsCancellationRequested do
        do! this.RunStep(stoppingToken)
        do! Task.Delay(tick, stoppingToken)
    with
    | :? OperationCanceledException ->
      logger.LogInformation($"Stopping {nameof(AutoLoopManager)}...")
    | ex ->
      logger.LogError($"{ex}")
  }

type AutoLoopManagers(opts: IOptions<NLoopOptions>, sp: IServiceProvider) =
  let managers = Dictionary<SupportedCryptoCode, AutoLoopManager>()
  do
    for c in opts.Value.OffChainCrypto do
      let man =
        new
          AutoLoopManager(
            sp.GetService<_>(),
            sp.GetService<_>(),
            sp.GetService<_>(),
            sp.GetService<_>(),
            sp.GetService<_>(),
            sp.GetService<_>(),
            sp.GetService<_>(),
            sp.GetService<_>(),
            sp.GetService<_>(),
            sp.GetService<_>(),
            c,
            sp.GetService<_>()
            )
      managers.Add(c, man)

  member val Managers = managers with get

  interface IHostedService with
    member this.StartAsync(cancellationToken) =
      managers
      |> Seq.map(fun kv -> kv.Value.StartAsync(cancellationToken))
      |> Task.WhenAll
    member this.StopAsync(cancellationToken) =
      managers
      |> Seq.map(fun kv -> kv.Value.StopAsync(cancellationToken))
      |> Task.WhenAll
type TryGetAutoLoopManager = SupportedCryptoCode -> AutoLoopManager option
