namespace NLoop.Server.Services

open System
open System.Linq
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open DotNetLightning.Utils.Primitives
open FsToolkit.ErrorHandling
open LndClient
open LndClient
open Microsoft.Extensions.Hosting
open FSharp.Control.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.RPC
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.Actors
open NLoop.Server.DTOs
open NLoop.Server.Projections
open NLoop.Server.Services


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
type SwapDisqualifiedReason =
  | None
  | BudgetNotStarted
  | SweepFeesTooHigh
  | BudgetElapsed
  | InFlightLimitReached
  | SwapFeeTooHigh
  | MinerFeeTooHigh
  | PrepayTooHigh
  | FailureBackoff
  | LoopOutAlreadyInTheChannel
  | LoopInAlreadyInTheChannel
  | LiquidityOk

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

[<AutoOpen>]
module private Constants =
  /// We use static fee rate to estimate our sweep fee, because we can't realistically
  /// estimate what our fee estimate will be by the time we reach timeout. We set this to a
  /// high estimate so that we can account for worst-case fees, (1250 * 4 / 1000) = 50 sat/byte
  let defaultLoopInSweepFee = FeeRate(1250m)


[<AutoOpen>]
module private Helpers =
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

  /// Calculates the largest possible fees for a loop out swap,
  /// comparing the fees for a successful swap to the cost when the client pays
  /// the prepay because they failed to sweep the on chain htlc. This is unlikely,
  let worstCaseOutFees
    (maxPrepayRoutingFee: Money voption)
    (maxSwapRoutingFee: Money voption)
    (maxSwapFee: Money voption)
    (maxMinerFee: Money voption)
    (maxPrepayAmount: Money voption): Money voption =
    maxPrepayRoutingFee |> ValueOption.bind(fun f1 ->
      maxMinerFee |> ValueOption.bind(fun f2 ->
        maxSwapFee |> ValueOption.bind(fun f3 ->
          maxSwapRoutingFee |> ValueOption.bind(fun f4 ->
            maxPrepayAmount |> ValueOption.map(fun f5 ->
              let successFees = f1 + f2 + f3 + f4
              let noShowFees = f1 + f5
              if noShowFees > successFees then
                noShowFees
              else successFees
            )
          )
        )
      )
    )

type Rules = {
  ChannelRules: Map<ShortChannelId, ThresholdRule>
  PeerRules: Map<NodeId, ThresholdRule>
}
  with
  static member Zero = {
    ChannelRules = Map.empty
    PeerRules = Map.empty
  }

[<RequireQualifiedAccess>]
type SwapSuggestion =
  | Out of LoopOutRequest
  with
  member this.Fees(): Money =
    match this with
    | Out req ->
      worstCaseOutFees
        req.MaxPrepayRoutingFee
        req.MaxSwapRoutingFee
        req.MaxSwapFee
        req.MaxMinerFee
        req.MaxPrepayAmount
      |> function _ -> failwith "todo"

  member this.Amount: Money =
    match this with
    | Out req ->
      req.Amount
  member this.Channels: ShortChannelId [] =
    match this with
    | Out req ->
      req.ChannelId

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

[<AutoOpen>]
module private Fees =
  type IFeeLimit =
    abstract member Validate: unit -> Result<unit, string>
    /// Checks whether we may dispatch a loop out swap based on the current fee conditions.
    abstract member MayLoopOut: FeeRate -> Result<unit, SwapDisqualifiedReason>
    /// Checks whether the quote provided is within our fee limits for the swap amount.
    abstract member LoopOutLimits: swapAmount: Money * LoopOutQuote -> Task<Result<unit, SwapDisqualifiedReason>>
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
}
  with
  static member Default = {
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
  }
  /// Checks whether a set of parameters is valid.
  member this.Validate(minConf: int32, openChannels: ListChannelResponse seq, server) =
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

      if (this.SweepConfTarget.Value |> int < minConf) then
        return! Error $"confirmation target must be at least: %d{minConf}"

      do! this.SweepFeeRateLimit.Validate()
      if this.AutoFeeBudget < Money.Zero then
        return! Error $"Negative Budget {this}"

      if this.MaxAutoInFlight <= 0 then
        return! Error $"Zero In Flight {this}"

      do!
        validateRestrictions(server, this.ClientRestrictions)
        |> Result.mapError(fun e -> e.Message)
      return Ok()
    }

type Config = {
  RPCClient: RPCClient
  BoltzClient: BoltzClient
  Restrictions: Swap.Category -> Task<Result<Restrictions, string>>
  Lnd: INLoopLightningClient
  LoopOutQuote: LoopOutQuoteRequest -> Task<LoopOutQuote>
  LoopInQuote: LoopInQuoteRequest -> Task<LoopInQuote>
  MinConfirmation: BlockHeightOffset32
  SwapActor: SwapActor
  /// conf target for estimating a fee for creating swap tx in loopin.
  HTLCConfTarget: BlockHeightOffset32
  Logger: ILogger
}

type SuggestSwapError =
  | SwapDisqualified of SwapDisqualifiedReason
  | Other of string

type SwapTraffic = {
  OngoingLoopOut: Map<ShortChannelId, bool>
  OngoingLoopIn: Map<NodeId, bool>
  FailedLoopOut: Map<ShortChannelId, DateTimeOffset>
}
  with
  /// returns a boolean that indicates whether we may perform a swap for a peer and its set of channels.
  member this.MaySwap(peer: NodeId, channels: ShortChannelId seq, logger: ILogger) = result {
      for chanId in channels do
        let recentFail, lastFail = this.FailedLoopOut.TryGetValue chanId
        if recentFail then
          logger.LogDebug($"Channel: {chanId} (i.e. {chanId.ToUInt64()}) not eligible for suggestions. Part of a failed swap at {lastFail}")
          return! Error SwapDisqualifiedReason.FailureBackoff
        else if this.OngoingLoopOut |> Seq.exists(fun kv -> kv.Key = chanId) then
          logger.LogDebug($"Channel {chanId} (i.e. {chanId.ToUInt64()} not eligible for suggestions. ongoing loop out utilizing channel")
          return! Error SwapDisqualifiedReason.LoopOutAlreadyInTheChannel
      failwith "todo"
    }

type TargetPeerOrChannel = {
  Peer: NodeId
  Channels: ShortChannelId array
}
type SwapBuilder = {
  SwapType: Swap.Category
  /// Validate our swap is able to execute according to the current state of fee market.
  MaySwap: Parameters -> Task<Result<unit, SwapDisqualifiedReason>>
  /// Examines our current swap traffic to determine whether we should suggest the builder's type of swap for the peer
  /// and channels suggested.
  InUse: SwapTraffic -> TargetPeerOrChannel -> Task<Result<unit, SwapDisqualifiedReason>>
  BuildSwap: TargetPeerOrChannel -> Money -> PairId -> bool -> Parameters -> Task<Result<SwapSuggestion, SwapDisqualifiedReason>>
}
  with
  static member NewLoopOut(cfg: Config): SwapBuilder =
    {
      SwapType = Swap.Category.Out
      MaySwap = fun parameters -> task {
        let! resp = cfg.RPCClient.EstimateSmartFeeAsync(parameters.SweepConfTarget.Value |> int)
        return parameters.SweepFeeRateLimit.MayLoopOut(resp.FeeRate)
      }
      InUse = fun traffic { Peer = peer; Channels = channels } -> taskResult {
        for chanId in channels do
          match traffic.FailedLoopOut.TryGetValue(chanId) with
          | true, lastFailedSwap ->
            // there is a recently failed swap.
            cfg.Logger.LogDebug($"channel: {chanId} ({chanId.ToUInt64()}) not eligible for suggestions.
                                It was a part of the failed swap at: {lastFailedSwap}")
            return! Error(SwapDisqualifiedReason.FailureBackoff)
          | false, _ -> ()
          match traffic.OngoingLoopOut.TryGetValue chanId with
          | true, _ ->
            cfg.Logger.LogDebug($"Channel: {chanId} ({chanId.ToUInt64()}) not eligible for suggestions.
                                Ongoing loop out utilizing channel.")
            return! Error(SwapDisqualifiedReason.LoopOutAlreadyInTheChannel)
          | false, _ -> ()

        match traffic.OngoingLoopIn.TryGetValue peer with
        | true, _ ->
          return! Error(SwapDisqualifiedReason.LoopInAlreadyInTheChannel)
        | false, _ -> ()
        return ()
      }
      BuildSwap = fun { Peer = peer; Channels = channels } amount pairId autoloop parameters -> taskResult {
        let! quote =
          let req =
            { LoopOutQuoteRequest.pair = pairId
              Amount = amount
              SweepConfTarget = parameters.SweepConfTarget }
          cfg.LoopOutQuote(req)
        do! parameters.SweepFeeRateLimit.LoopOutLimits(amount, quote)
        let prepayMaxFee, routeMaxFee, minerFee = parameters.SweepFeeRateLimit.LoopOutFees(amount, quote)
        let f = cfg.BoltzClient.CreateReverseSwapAsync
        let height = failwith "todo"
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
          MaxPrepayAmount = failwith "todo"
          MaxMinerFee = minerFee |> ValueSome
          SweepConfTarget = parameters.SweepConfTarget.Value |> int |> ValueSome
        }
        let! _ =
          cfg.SwapActor.ExecNewLoopOut(req, height)
          |> TaskResult.mapError(fun _ -> failwith "todo")
        return
          failwith "todo"
      }
    }

  static member NewLoopIn(cfg: Config) =
    {
      InUse = fun (traffic: SwapTraffic) ({ Channels = channels; Peer = peers }: TargetPeerOrChannel) ->
        for chanId in channels do
          let lastFail = traffic.FailedLoopOut.[chanId]
          ()
        failwith "todo"
      SwapType = failwith "todo"
      MaySwap = failwith "todo"
      BuildSwap = failwith "todo"
    }

type AutoLoopManager(logger: ILogger<AutoLoopManager>,
                     opts: IOptions<NLoopOptions>,
                     projection: SwapStateProjection,
                     boltzClient: BoltzClient,
                     cfg: Config,
                     blockChainListener: BlockchainListener,
                     lightningClientProvider: ILightningClientProvider)  =
  inherit BackgroundService()
  member val Parameters = Parameters.Default with get, set
  member val LoopOutSwapBuilder = SwapBuilder.NewLoopOut(cfg)

  /// Examines our existing swaps and returns a summary of the current activity which can be used
  /// to determine wheter we should perform any swaps.
  member private this.CurrentSwapTraffic(loopOut, loopIn): SwapTraffic =
    failwith "todo"

  /// Queries the server for its latest swap size restrictions,
  /// validates client restrictions (if present) against these values and merges the client's custom
  /// requirements with the server's limits to produce a single set of limitations for our swap.
  member private this.GetSwapRestrictions(swapType: Swap.Category, cryptoPair: PairId) = taskResult {
      let! p = boltzClient.GetPairsAsync()
      let restrictions = p.Pairs.[PairId.toString(&cryptoPair)].Limits
      do!
          let serverRestrictions =
            {
              Minimum = restrictions.Minimal |> Money.Satoshis
              Maximum = restrictions.Maximal |> Money.Satoshis
            }
          validateRestrictions(
            serverRestrictions,
            this.Parameters.ClientRestrictions
          )
      return
        { restrictions with
            Minimal = Math.Max(this.Parameters.ClientRestrictions.Minimum.Satoshi, restrictions.Minimal)
            Maximal =
              if this.Parameters.ClientRestrictions.Maximum.Satoshi <> 0L && this.Parameters.ClientRestrictions.Maximum.Satoshi < restrictions.Maximal then
                this.Parameters.ClientRestrictions.Maximum.Satoshi
              else
                restrictions.Maximal
          }
    }

  member private this.SingleReasonSuggestion(reason: SwapDisqualifiedReason): SwapSuggestions =
    let r = this.Parameters.Rules
    { SwapSuggestions.Zero
        with
        DisqualifiedChannels = r.ChannelRules |> Map.map(fun _ _ -> reason)
        DisqualifiedPeers = r.PeerRules |> Map.map(fun _ _ -> reason)
    }

  member this.SuggestSwaps(cryptoPair: PairId, autoloop: bool, ?ct: CancellationToken): Task<SwapSuggestions> = task {
    let ct = defaultArg ct CancellationToken.None
    if this.Parameters.AutoFeeStartDate > DateTimeOffset.Now then return this.SingleReasonSuggestion(SwapDisqualifiedReason.BudgetNotStarted) else

    match! this.LoopOutSwapBuilder.MaySwap(this.Parameters) with
    | Error e ->
      return this.SingleReasonSuggestion(e)
    | Ok() ->
      let! restrictions = this.GetSwapRestrictions(Swap.Category.Out, cryptoPair)
      let onGoingLoopOuts: _ list = projection.OngoingLoopOuts |> Seq.toList
      let onGoingLoopIns: _ list = projection.OngoingLoopIns |> Seq.toList
      let summary =
        ExistingAutoLoopSummary.FromLoopInOuts(onGoingLoopIns, onGoingLoopOuts)
      let existingAutoLoopOuts =
        onGoingLoopOuts |> List.filter(fun o -> o.Label = Labels.autoLoopLabel(Swap.Category.Out))

      let allowedSwaps = opts.Value.MaxAutoInFlight - onGoingLoopOuts.Length
      if summary.TotalFees >= this.Parameters.AutoFeeBudget then
        logger.LogDebug($"autoloop fee budget: %d{this.Parameters.AutoFeeBudget.Satoshi} satoshi exhausted, "+
                        $"%d{summary.SpentFees.Satoshi} sats spent on completed swaps, %d{summary.PendingFees.Satoshi} sats reserved for ongoing swaps " +
                        "(upper limit)")
        return this.SingleReasonSuggestion(SwapDisqualifiedReason.BudgetElapsed)
      elif opts.Value.MaxAutoInFlight < existingAutoLoopOuts.Length then
        logger.LogDebug($"%d{opts.Value.MaxAutoInFlight} autoloops allowed, %d{onGoingLoopOuts.Length} inflight")
        return this.SingleReasonSuggestion(SwapDisqualifiedReason.InFlightLimitReached)
      else
        let struct (offChainCrypto, _onChainCrypto) = cryptoPair
        let! channels =
          lightningClientProvider
            .GetClient(offChainCrypto)
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
            this.Parameters.Rules.PeerRules
            |> Map.tryFind(nodeId)
            |> Option.map(fun rule -> (nodeId, balance, rule))
          )

        let failedSwaps =
          projection.State
          |> Seq.choose(fun kv -> match kv.Value with | Swap.State.Finished(_, f) -> Some f | _ -> None)
          |> Seq.filter(function | Swap.FinishedState.Success _ -> false | _ -> true)

        let mutable resp = SwapSuggestions.Zero
        let mutable suggestions: ResizeArray<SwapSuggestion> = ResizeArray()
        let traffic = {
          SwapTraffic.FailedLoopOut = failedSwaps
          OngoingLoopOut = onGoingLoopOut
          OngoingLoopIn = onGoingLoopIn
        }
        do!
          task {
            for nodeId, balances, rule in peersWithRules do
              match! this.SuggestSwap(traffic, balances, rule, restrictions, autoloop) |> TaskResult.mapError(fun e -> failwith "todo") with
              | Error(SwapDisqualified e) ->
                resp <- { resp with DisqualifiedPeers = resp.DisqualifiedPeers |> Map.add nodeId e }
              | Error e ->
                failwith "todo"
                //return! Error e
              | Ok s ->
                suggestions.Add s
          }

        do!
          task {
          for c in channels do
            let balance = Balances.FromLndResponse c
            match this.Parameters.Rules.ChannelRules.TryGetValue c.Id with
            | false, _ -> ()
            | true, rule ->
              match! this.SuggestSwap(traffic, balance, rule, restrictions, autoloop) with
              | Error(SwapDisqualified e) ->
                resp <- { resp with DisqualifiedChannels = resp.DisqualifiedChannels |> Map.add c.Id e }
              | Error e ->
                failwith "todo"
                //return! Error e
              | Ok s ->
                suggestions.Add s
          }
        if suggestions.Count = 0 then
          return failwith "todo"
          //return! Ok resp
        else
          let suggestions = suggestions |> Seq.sortBy(fun s -> s.Amount)
          return failwith "todo"
        return failwith "todo"
    return ((failwith "todo"): SwapSuggestions)
  }

  member private this.SuggestSwap(traffic, balance: Balances, rule: ThresholdRule, restrictions: Restrictions, pairId, autoloop: bool): Task<Result<SwapSuggestion, SuggestSwapError>> =
    task {
      let peerOrChannel = { Peer = balance.PubKey; Channels = balance.Channels.ToArray() }
      match! this.LoopOutSwapBuilder.InUse(traffic) (peerOrChannel)  with
      | Error e ->
        return failwith "todo"
      | Ok() ->
        let amount = rule.SwapAmount(balance, restrictions, opts.Value.TargetIncomingLiquidityRatio)
        if amount = Money.Zero then return! Error SwapDisqualifiedReason.LiquidityOk else
        return!
          this.LoopOutSwapBuilder.BuildSwap peerOrChannel amount pairId  autoloop this.Parameters
          |> TaskResult.mapError(fun e -> failwith "todo")
    }

  /// Gets a set of suggested swaps and dispatches them automatically if we have automated looping enabled.
  member this.AutoLoop(inOrOut: Swap.Category, pairId: PairId): Task<Result<_, AutoLoopError>> = taskResult {
    let! suggestion = this.SuggestSwaps(inOrOut, pairId, true)

    for swap in suggestion.OutSwaps do
      if not <| this.Parameters.AutoLoop then
        let chanSet = swap.ChannelId |> Array.fold(fun acc c -> $"{acc},{c} ({c.ToUInt64()})") ""
        logger.LogDebug($"recommended autoloop out: {swap.Amount.Satoshi} sats over {chanSet}")
      else
        let req = boltzClient.CreateReverseSwapAsync
        let! loopOut =
          cfg.SwapActor.ExecNewLoopOut(swap, blockChainListener.CurrentHeight)
          |> TaskResult.mapError(AutoLoopError.FailedToDispatchLoop)
        logger.LogInformation($"loop out automatically dispatched.: (id {loopOut.Id}, onchain address: {loopOut.ClaimAddress}. amount: {loopOut.OnChainAmount.Satoshi} sats)")

    for inSwap in suggestion.InSwaps do
      if not <| this.Parameters.AutoLoop then
        logger.LogDebug($"recommended autoloop in: %d{inSwap.Amount.Satoshi} sats over {inSwap.ChannelId} ({inSwap.ChannelId |> Option.map(fun c -> c.ToUInt64())})")
      else
        let! loopIn =
          cfg.SwapActor.ExecNewLoopIn(inSwap, blockChainListener.CurrentHeight)
          |> TaskResult.mapError(AutoLoopError.FailedToDispatchLoop)
        logger.LogInformation($"loop in automatically dispatched: (id: {loopIn.Id})")

    return ()
  }

  override this.StartAsync(ct) = unitTask {
    let rules = failwith "todo"
    this.Parameters <- { this.Parameters with Rules = rules }
    //do! base.StartAsync(ct)
    return failwith "todo"
  }

  override this.ExecuteAsync(ct: CancellationToken) = unitTask {
    let onChainClis: (_ * _ * _) seq =
      opts.Value.OnChainCrypto
      |> Seq.distinct
      |> Seq.map(fun x ->
        (opts.Value.GetRPCClient x, lightningClientProvider.GetClient(x), x))

    let pairIds = ResizeArray()
    for _,_, baseAsset in onChainClis do
      for quoteAsset in opts.Value.OffChainCrypto do
        pairIds.Add(struct(baseAsset, quoteAsset))
    try
      while not <| ct.IsCancellationRequested do
        do! Task.Delay(30000)
        for p in pairIds do
          match! this.AutoLoop(Swap.Category.Out, p) with
          | Ok () -> ()
          | Error (AutoLoopError.NoRules as e) ->
            logger.LogDebug(e.Message)
          | Error e ->
            logger.LogError($"AutoLop failed: {e}")
    with
    | :? OperationCanceledException ->
      logger.LogInformation($"Stopping {nameof(AutoLoopManager)}...")
    | ex ->
      logger.LogError($"{ex}")
  }


