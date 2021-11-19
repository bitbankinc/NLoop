namespace NLoop.Server.Services

open System
open System.Collections.Generic
open System.Linq
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open FsToolkit.ErrorHandling
open LndClient
open Microsoft.Extensions.Hosting
open FSharp.Control.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Microsoft.Extensions.DependencyInjection
open NBitcoin
open NBitcoin.RPC
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.DTOs
open NLoop.Server.Projections
open NLoop.Server.RPCDTOs
open NLoop.Server.Services

[<AutoOpen>]
module private Constants =
  /// We use static fee rate to estimate our sweep fee, because we can't realistically
  /// estimate what our fee estimate will be by the time we reach timeout. We set this to a
  /// high estimate so that we can account for worst-case fees, (1250 * 4 / 1000) = 50 sat/byte
  let defaultLoopInSweepFee = FeeRate(1250m)

[<AutoOpen>]
module private Helpers =

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
  | MinimumExceedsMaximumAmt
  | NoRules
  | MaxExceedsServer of clientMax: Money * serverMax: Money
  | MinLessThenServer of clientMax: Money * serverMax: Money
  | ExclusiveRules
  | FailedToDispatchLoop of msg: string
  with
  member this.Message =
    match this with
    | NegativeBudget -> "SwapBudget must be >= 0"
    | ZeroInFlight -> "max in flight swap must be >= 0"
    | MinimumExceedsMaximumAmt -> "minimum swap amount exceeds maximum"
    | NoRules -> "No rules set for autoloop"
    | MaxExceedsServer (c, s) ->
      $"maximum swap amount ({c.Satoshi} sats) is more than the server maximum ({s.Satoshi} sats)"
    | MinLessThenServer (c, s)  ->
      $"minimum swap amount ({c.Satoshi} sats) is less than server minimum ({s.Satoshi} sats)"
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
      req.ChannelId
    | In _ -> [||]

  member this.Peers(knownChannels: Map<ShortChannelId, NodeId>, logger: ILogger): NodeId[] =
    match this with
    | Out req ->
      let knownPeers, unKnownPeers =
        req.ChannelId
        |> Array.partition(fun c -> knownChannels.Any(fun kc -> kc.Key = c))

      unKnownPeers
      |> Array.iter(fun c -> logger.LogWarning($"peer for channel: {c} (%d{c.ToUInt64()}) unknown"))

      knownPeers
      |> Array.map(fun shortChannelId -> knownChannels.TryGetValue(shortChannelId) |> snd)
    | In req ->
      req.LastHop |> Option.map(NodeId) |> Option.toArray

type SwapSuggestions = {
  OutSwaps: LoopOutRequest[]
  InSwaps: LoopInRequest[]
  DisqualifiedChannels: Map<ShortChannelId, SwapDisqualifiedReason>
  DisqualifiedPeers: Map<NodeId, SwapDisqualifiedReason>
}
  with
  static member Zero = {
    OutSwaps = [||]
    InSwaps = [||]
    DisqualifiedChannels = Map.empty
    DisqualifiedPeers = Map.empty
  }

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
      IncomingSat = resp.LocalBalance
      OutGoingSat = resp.Cap - resp.LocalBalance
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

type Restrictions = {
  Minimum: Money
  Maximum: Money
}
  with
  static member FromBoltzResponse(limit: ServerLimit) = {
    Minimum = limit.Minimal |> Money.Satoshis
    Maximum = limit.Maximal |> Money.Satoshis
  }

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
        if channelBalances.IncomingSat < minimumInComing then Money.Zero else
        // if we are already below the threshold set for outgoing capacity, we cannot take any further action.
        if channelBalances.OutGoingSat < minimumInComing then Money.Zero else
        let targetPoint =
          let maximumIncoming = channelBalances.CapacitySat - minimumOutGoing
          let midPoint = (minimumInComing + maximumIncoming) / 2L
          midPoint * int64 targetIncomingLiquidityRatio
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

  let validateRestrictions({Minimum = serverMin; Maximum =  serverMax}: Restrictions,
                           {Minimum = clientMin; Maximum = clientMax }: Restrictions) =
    let zeroMin = clientMin = Money.Zero
    let zeroMax = clientMax = Money.Zero
    if zeroMin && zeroMax then Ok() else
    if not <| zeroMax && clientMin > clientMax then
      Error(AutoLoopError.MinimumExceedsMaximumAmt)
    elif not <| zeroMax && clientMax > serverMax then
      Error(AutoLoopError.MaxExceedsServer(clientMax, serverMax))
    elif zeroMin then
      Ok()
    elif clientMin < serverMin then
      Error(AutoLoopError.MinLessThenServer(clientMin, serverMin))
    else
      Ok()

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
module private Fees =
  type IFeeLimit =
    abstract member Validate: unit -> Result<unit, string>
    /// Checks whether we may dispatch a loop out swap based on the current fee conditions.
    abstract member MayLoopOut: feeRate: FeeRate -> Result<unit, SwapDisqualifiedReason>
    /// Checks whether the quote provided is within our fee limits for the swap amount.
    abstract member LoopOutLimits: swapAmount: Money * quote: LoopOutQuote -> Task<Result<unit, SwapDisqualifiedReason>>
    abstract member LoopOutFees: amount: Money * quote: LoopOutQuote -> Money * Money * Money
    abstract member LoopInLimits: amount: Money * quote: LoopInQuote -> Task<Result<unit, SwapDisqualifiedReason>>

  type FeePortion = {
    PartsPerMillion: uint64
  }
    with
    interface IFeeLimit with
      member this.Validate() =
        if this.PartsPerMillion <= 0UL then
          Error "Invalid Parts per million"
        else
          Ok()
      member this.LoopOutLimits(swapAmount, quote) =
        failwith "todo"

      member this.LoopOutFees(amount, quote) =
        let feeLimit = amount.Satoshi
        failwith "todo"

      member this.LoopInLimits(amount, quote) =
        failwith "todo"

      member this.MayLoopOut(feeRate) = failwith "todo"

/// run-time modifiable part of the auto loop parameters.
type Parameters = private {
  AutoFeeBudget: Money
  AutoFeeStartDate: DateTimeOffset
  MaxAutoInFlight: int
  FailureBackoff: TimeSpan
  SweepConfTarget: BlockHeightOffset32
  HTLCConfTarget: BlockHeightOffset32
  SweepFeeRateLimit: IFeeLimit
  ClientRestrictions: Restrictions
  Rules: Rules
  AutoLoop: bool
  SwapCategory: Swap.Category
}
  with
  static member DefaultOut = {
    AutoFeeBudget = failwith "todo"
    AutoFeeStartDate = DateTimeOffset.UtcNow
    MaxAutoInFlight = 1
    FailureBackoff = TimeSpan.FromHours(6.)
    SweepConfTarget = 100u |> BlockHeightOffset32
    SweepFeeRateLimit = { FeePortion.PartsPerMillion = 750UL }
    ClientRestrictions = failwith "todo"
    HTLCConfTarget = failwith "todo"
    Rules = Rules.Zero
    AutoLoop = false
    SwapCategory = Swap.Category.Out
  }

  static member DefaultIn = {
    AutoFeeBudget = failwith "todo"
    AutoFeeStartDate = DateTimeOffset.UtcNow
    MaxAutoInFlight = 1
    FailureBackoff = TimeSpan.FromHours(6.)
    SweepConfTarget = 100u |> BlockHeightOffset32
    SweepFeeRateLimit = { FeePortion.PartsPerMillion = 750UL }
    ClientRestrictions = failwith "todo"
    HTLCConfTarget = failwith "todo"
    Rules = Rules.Zero
    AutoLoop = false
    SwapCategory = Swap.Category.In
  }
  member this.OffChainAsset pairId =
    match this.SwapCategory with
    | Swap.Category.Out ->
      let struct (_baseAsset, quoteAsset) = pairId
      quoteAsset
    | Swap.Category.In ->
      let struct (baseAsset, _quoteAsset) = pairId
      baseAsset
  /// Checks whether a set of parameters is valid.
  member this.Validate(minConf: BlockHeightOffset32, openChannels: ListChannelResponse seq, server): Result<unit, _> =
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

      if (this.SweepConfTarget < minConf) then
        return! Error $"confirmation target must be at least: %d{minConf.Value}"

      do! this.SweepFeeRateLimit.Validate()
      if this.AutoFeeBudget < Money.Zero then
        return! Error $"Negative Budget {this}"

      if this.MaxAutoInFlight <= 0 then
        return! Error $"Zero In Flight {this}"

      do!
        validateRestrictions(server, this.ClientRestrictions)
        |> Result.mapError(fun e -> e.Message)
      return ()
    }

type Config = {
  RPCClient: RPCClient
  BoltzClient: BoltzClient
  Restrictions: Swap.Category -> Task<Result<Restrictions, string>>
  Lnd: INLoopLightningClient
  MinConfirmation: BlockHeightOffset32
  SwapActor: SwapActor
}

type SuggestSwapError =
  | SwapDisqualified of SwapDisqualifiedReason
  | Other of string

type SwapTraffic = {
  OngoingLoopOut: list<ShortChannelId>
  OngoingLoopIn: list<NodeId>
  FailedLoopOut: Map<ShortChannelId, DateTime>
  FailedLoopIn: Map<NodeId, DateTime>
}
  with
  /// returns a boolean that indicates whether we may perform a swap for a peer and its set of channels.
  member this.MaySwap(peer: NodeId, channels: ShortChannelId seq, logger: ILogger) = result {
      for chanId in channels do
        let recentFail, lastFail = this.FailedLoopOut.TryGetValue chanId
        if recentFail then
          logger.LogDebug($"Channel: {chanId} (i.e. {chanId.ToUInt64()}) not eligible for suggestions. Part of a failed swap at {lastFail}")
          return! Error SwapDisqualifiedReason.FailureBackoff
        else if this.OngoingLoopOut |> Seq.contains(chanId) then
          logger.LogDebug($"Channel {chanId} (i.e. {chanId.ToUInt64()} not eligible for suggestions. ongoing loop out utilizing channel")
          return! Error SwapDisqualifiedReason.LoopOutAlreadyInTheChannel
      failwith "todo"
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
  BuildSwap: TargetPeerOrChannel -> Money -> PairId -> bool -> Parameters -> Task<Result<SwapSuggestion, SwapDisqualifiedReason>>
}
  with
  static member NewLoopOut(cfg: Config, pairId: PairId, logger: ILogger): SwapBuilder =
    {
      MaySwap = fun parameters -> task {
        let! resp = cfg.RPCClient.EstimateSmartFeeAsync(parameters.SweepConfTarget.Value |> int)
        return parameters.SweepFeeRateLimit.MayLoopOut(resp.FeeRate)
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
      BuildSwap = fun { Peer = peer; Channels = channels } amount pairId autoloop parameters -> taskResult {
        let! quote =
          let req =
            { LoopOutQuoteRequest.pair = pairId
              Amount = amount
              SweepConfTarget = parameters.SweepConfTarget }
          cfg.BoltzClient.GetLoopOutQuote(req)
        do! parameters.SweepFeeRateLimit.LoopOutLimits(amount, quote)
        let prepayMaxFee, routeMaxFee, minerFee = parameters.SweepFeeRateLimit.LoopOutFees(amount, quote)
        let! addr =
            if autoloop then
              cfg.Lnd.GetDepositAddress()
              |> Task.map Some
            else
              Task.FromResult None
        let req = {
          LoopOutRequest.Address = addr
          ChannelId = channels
          PairId = pairId |> Some
          Amount = amount
          HtlcConfTarget = parameters.HTLCConfTarget.Value |> int32 |> Some
          Label =
            if autoloop then
              Labels.autoLoopLabel(Swap.Category.Out) |> Some
            else
              None
          MaxSwapRoutingFee = routeMaxFee |> ValueSome
          MaxPrepayRoutingFee = prepayMaxFee |> ValueSome
          MaxSwapFee = quote.SwapFee |> ValueSome
          MaxPrepayAmount = quote.PrepayAmount |> ValueSome
          MaxMinerFee = minerFee |> ValueSome
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
      BuildSwap = fun { Channels = channels; Peer = peer } amount pairId autoloop parameters -> taskResult {
        let! quote = cfg.BoltzClient.GetLoopInQuote({ Amount = amount })
        do! parameters.SweepFeeRateLimit.LoopInLimits(amount, quote)
        let req = {
          LoopInRequest.Amount = amount
          ChannelId = None
          Label = if autoloop then Labels.autoLoopLabel(Swap.Category.In) |> Some else None
          PairId = Some pairId
          MaxMinerFee = quote.MinerFee |> ValueSome
          MaxSwapFee = quote.SwapFee |> ValueSome
          HtlcConfTarget = parameters.HTLCConfTarget.Value |> int |> ValueSome
          RouteHints = failwith "todo"
        }
        return
          SwapSuggestion.In(req)
      }
    }

type AutoLoopManager(logger: ILogger<AutoLoopManager>,
                     opts: IOptions<NLoopOptions>,
                     swapStateProjection: SwapStateProjection,
                     recentSwapFailureProjection: RecentSwapFailureProjection,
                     boltzClient: BoltzClient,
                     blockChainListener: IBlockChainListener,
                     swapActor: SwapActor,
                     _lightningClientProvider: ILightningClientProvider) =

  inherit BackgroundService()

  let mutable parametersDict = Map.empty<PairId, Parameters>

  let _lockObj = obj()

  member this.Parameters
    with get () = parametersDict
    and private set (v: Map<PairId, Parameters>) =
      lock _lockObj (fun () -> parametersDict <- v)

  member this.Config (pairId: PairId) =
    let p = this.Parameters.[pairId]
    let struct(onChain, offChain) =
      match p.SwapCategory with
      | Swap.Category.Out ->
        pairId
      | Swap.Category.In ->
        let struct(b, q) = pairId
        struct(q, b)
    {
      Config.Restrictions = failwith "todo"
      RPCClient = opts.Value.GetRPCClient onChain
      BoltzClient = boltzClient
      Lnd = _lightningClientProvider.GetClient offChain
      MinConfirmation = Constants.MinConfTarget |> uint32 |> BlockHeightOffset32
      SwapActor = swapActor
    }

  member this.Builder pairId =
    let p = this.Parameters.[pairId]
    let c = this.Config pairId
    match p.SwapCategory with
    | Swap.Category.Out ->
      SwapBuilder.NewLoopOut(c, pairId, logger)
    | Swap.Category.In ->
      SwapBuilder.NewLoopIn(c, logger)

  member private this.OffChainAsset pairId =
    let p = this.Parameters.[pairId]
    p.OffChainAsset pairId

  member this.LightningClient pairId =
    this.OffChainAsset pairId
    |> _lightningClientProvider.GetClient

  member this.SetParameters(pairId: PairId, v: Parameters) = taskResult {
    match this.Parameters |> Map.tryFind pairId with
    | None ->
      return! Error $"PairId {PairId.toString(&pairId)} not supported in autoloop"
    | Some p ->
      let! channels =
        _lightningClientProvider
          .GetClient(this.OffChainAsset pairId)
          .ListChannels()
      let c = this.Config pairId
      let! r =
        c.Restrictions p.SwapCategory
      do! v.Validate(c.MinConfirmation, channels, r)
      this.Parameters <-
        this.Parameters |> Map.add pairId v
  }

  /// Query the server for its latest swap size restrictions,
  /// validates client restrictions (if present) against these values and merges the client's custom
  /// requirements with the server's limits to produce a single set of limitations for our swap.
  member private this.GetSwapRestrictions(swapType: Swap.Category, cryptoPair: PairId): Task<Result<_, AutoLoopError>> = taskResult {
      let! p = boltzClient.GetPairsAsync()
      let restrictions = p.Pairs.[PairId.toString(&cryptoPair)].Limits
      let par = this.Parameters.[cryptoPair]
      do!
          validateRestrictions(
            Restrictions.FromBoltzResponse(restrictions),
            par.ClientRestrictions
          )
      return
        {
          Minimum = Money.Max(par.ClientRestrictions.Minimum, restrictions.Minimal |> Money.Satoshis)
          Maximum =
            if par.ClientRestrictions.Maximum <> Money.Zero && par.ClientRestrictions.Maximum.Satoshi < restrictions.Maximal then
              par.ClientRestrictions.Maximum
            else
              restrictions.Maximal |> Money.Satoshis
        }
    }

  member private this.SingleReasonSuggestion(pairId: PairId, reason: SwapDisqualifiedReason): SwapSuggestions =
    let r = this.Parameters.[pairId].Rules
    { SwapSuggestions.Zero
        with
        DisqualifiedChannels = r.ChannelRules |> Map.map(fun _ _ -> reason)
        DisqualifiedPeers = r.PeerRules |> Map.map(fun _ _ -> reason)
    }

  member private this.CheckAutoLoopIsPossible(pairId, existingAutoLoopOuts: _ list, summary: ExistingAutoLoopSummary) =
    let par = this.Parameters.[pairId]
    let checkInFlightNumber () =
      if opts.Value.MaxAutoInFlight < existingAutoLoopOuts.Length then
        logger.LogDebug($"%d{opts.Value.MaxAutoInFlight} autoloops allowed, %d{existingAutoLoopOuts.Length} inflight")
        Error (this.SingleReasonSuggestion(pairId, SwapDisqualifiedReason.InFlightLimitReached))
      else
        Ok()
    let checkDate() =
      if par.AutoFeeStartDate > DateTimeOffset.UtcNow then
        Error <| this.SingleReasonSuggestion(pairId, SwapDisqualifiedReason.BudgetNotStarted)
      else
        Ok()
    result {
      do! checkDate()
      do! checkInFlightNumber()
      do! summary.ValidateAgainstBudget(par.AutoFeeBudget, logger)
          |> Result.mapError(fun r -> this.SingleReasonSuggestion(pairId, r))
    }

  member this.SuggestSwaps(autoloop: bool, pairId: PairId, ?ct: CancellationToken): Task<Result<SwapSuggestions, AutoLoopError>> = task {
    let ct = defaultArg ct CancellationToken.None
    let builder = this.Builder pairId
    let par = this.Parameters.[pairId]
    match! builder.MaySwap(par) with
    | Error e ->
      return this.SingleReasonSuggestion(pairId, e) |> Ok
    | Ok() ->
      match! this.GetSwapRestrictions(par.SwapCategory, pairId) with
      | Error e -> return Error e
      | Ok restrictions ->
      let onGoingLoopOuts: _ list = swapStateProjection.OngoingLoopOuts |> Seq.toList
      let onGoingLoopIns: _ list = swapStateProjection.OngoingLoopIns |> Seq.toList
      let summary =
        ExistingAutoLoopSummary.FromLoopInOuts(onGoingLoopIns, onGoingLoopOuts)
      let existingAutoLoopOuts =
        onGoingLoopOuts
        |> Seq.toList
      match this.CheckAutoLoopIsPossible(pairId, existingAutoLoopOuts, summary) with
      | Error e ->
        return e |> Ok
      | Ok () ->
        let! channels =
          (this.LightningClient pairId)
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
          SwapTraffic.FailedLoopOut = recentSwapFailureProjection.FailedLoopOuts
          FailedLoopIn = recentSwapFailureProjection.FailedLoopIns
          OngoingLoopOut =
            onGoingLoopOuts
            |> List.map(fun o -> o.OutgoingChanIds |> Array.toList) |> List.concat
          OngoingLoopIn =
            onGoingLoopIns
            |> List.map(fun i -> i.LastHop |> Option.map(NodeId) |> Option.toList)
            |> List.concat
        }

        for nodeId, balances, rule in peersWithRules do
          match! this.SuggestSwap(traffic, balances, rule, restrictions, pairId,  autoloop) |> TaskResult.mapError(fun e -> failwith "todo") with
          | Error(SwapDisqualified e) ->
            resp <- { resp with DisqualifiedPeers = resp.DisqualifiedPeers |> Map.add nodeId e }
          | Error e ->
            failwith "todo"
            //return! Error e
          | Ok (SwapSuggestion.In s) ->
            // Create a route_hint for every channel against the last_hop
            // to tell them which channel we want the inbound liquidity for.
            let targetChannels =
              channels
              |> List.filter(fun c -> match s.LastHop with | None -> false | Some lastHop -> lastHop = c.NodeId)
            let! chanInfos =
              targetChannels
              |> Seq.map(fun c -> task {
                let! resp = (this.LightningClient pairId).GetChannelInfo(c.Id)
                return (resp, c.Id)
              })
              |> Task.WhenAll
            let s =
              let routeHints: RouteHint[] =
                chanInfos
                |> Seq.map(fun (c, cId) -> {
                  HopHint.NodeId = c.Node1Policy.Id
                  HopHint.ChanId = cId
                  FeeBaseMSat = c.Node1Policy.FeeBase.MilliSatoshi
                  FeeProportionalMillionths = c.Node1Policy.FeeProportionalMillionths.MilliSatoshi
                  CltvExpiryDelta = c.Node1Policy.TimeLockDelta.Value |> int
                })
                |> Seq.map(fun h -> { RouteHint.Hops = [|h|] })
                |> Seq.toArray
              { s with RouteHints = routeHints }
            suggestions.Add (SwapSuggestion.In s)
          | Ok s ->
            suggestions.Add(s)

        for c in channels do
          let balance = Balances.FromLndResponse c
          match par.Rules.ChannelRules.TryGetValue c.Id with
          | false, _ -> ()
          | true, rule ->
            match! this.SuggestSwap(traffic, balance, rule, restrictions, pairId, autoloop) with
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
          let resp =
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
                acc, (subtractFee available)
            ) (resp, available)

          let _ =
            suggestions
            |> Seq.map(fun s ->
              /// If the maximum fee we expect our swap to use is less than the amount we have available, we add it to
              /// our set of swaps that fall within the budget and decrement our available amount.
              if s.Fees() <= available then
                available
                ()
              else
                ()
            )

          return failwith "todo"
  }

  member private this.SuggestSwap(traffic, balance: Balances, rule: ThresholdRule, restrictions: Restrictions, pairId, autoloop: bool): Task<Result<SwapSuggestion, SwapDisqualifiedReason>> =
    taskResult {
      let builder = this.Builder pairId
      let peerOrChannel = { Peer = balance.PubKey; Channels = balance.Channels.ToArray() }
      do! builder.VerifyTargetIsNotInUse(traffic) (peerOrChannel)

      let amount = rule.SwapAmount(balance, restrictions, opts.Value.TargetIncomingLiquidityRatio)
      if amount = Money.Zero then
        return! Error(SwapDisqualifiedReason.LiquidityOk)
      else
        let par = this.Parameters.[pairId]
        return!
          builder.BuildSwap peerOrChannel amount pairId  autoloop par
    }

  /// Gets a set of suggested swaps and dispatches them automatically if we have automated looping enabled.
  member this.AutoLoop(pairId, ct): Task<Result<_, AutoLoopError>> = taskResult {
    let! suggestion = this.SuggestSwaps(true, pairId, ct)

    for swap in suggestion.OutSwaps do
      let par = this.Parameters.[pairId]
      if not <| par.AutoLoop then
        let chanSet = swap.ChannelId |> Array.fold(fun acc c -> $"{acc},{c} ({c.ToUInt64()})") ""
        logger.LogDebug($"recommended autoloop out: {swap.Amount.Satoshi} sats over {chanSet}")
      else
        let! loopOut =
          swapActor.ExecNewLoopOut(swap, blockChainListener.CurrentHeight)
          |> TaskResult.mapError(AutoLoopError.FailedToDispatchLoop)
        logger.LogInformation($"loop out automatically dispatched.: (id {loopOut.Id}, onchain address: {loopOut.ClaimAddress}. amount: {loopOut.OnChainAmount.Satoshi} sats)")

    for inSwap in suggestion.InSwaps do
      let par = this.Parameters.[pairId]
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
        for pairId, _ in this.Parameters |> Map.toSeq do
          match! this.AutoLoop(pairId, stoppingToken) with
          | Ok() -> ()
          | Error e ->
            logger.LogError($"Error in autoloop ({PairId.toString(&pairId)}): {e}")
        return failwith "todo"
    with
    | :? OperationCanceledException ->
      logger.LogInformation($"Stopping {nameof(AutoLoopManager)}...")
    | ex ->
      logger.LogError($"{ex}")
  }
