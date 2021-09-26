namespace NLoop.Server.Services

open System
open System.Linq
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open DotNetLightning.Utils.Primitives
open FsToolkit.ErrorHandling
open LndClient
open Microsoft.Extensions.Hosting
open FSharp.Control.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.DTOs
open NLoop.Server.Projections


type [<Measure>] percent

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

type SwapSuggestions = {
  OutSwaps: LoopOutRequest []
  DisqualifiedChannels: Map<ShortChannelId, SwapDisqualifiedReason>
  DisqualifiedPeers: Map<NodeId, SwapDisqualifiedReason>
}
  with
  static member Zero = {
    OutSwaps = [||]
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

type Restrictions = {
  Minimum: Money
  Maximum: Money
}
[<AutoOpen>]
module private Helpers =
  let validateRestrictions(server: Restrictions, client: Restrictions) =
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

[<RequireQualifiedAccess>]
type SwapSuggestion =
  | Out of LoopOutRequest
  with
  member this.Fees(): Money =
    match this with
    | Out req ->
      failwith "todo"

  member this.Amount: Money =
    match this with
    | Out req ->
      req.Amount
  member this.Channels: ShortChannelId [] =
    match this with
    | Out req ->
      let c = req.ChannelId
      failwith "todo"

  member this.Peers: NodeId[] =
    failwith "todo"

type FeeLimit = {
  Validate: unit -> Result<unit, string>
  MayLoopOut: FeeRate -> Result<unit, string>
  LoopOutLimits: Money -> LoopOutQuote -> Result<unit, string>
  LoopOutFees: Money -> LoopOutQuote -> Money * Money * Money
}

type Parameters = {
  AutoFeeBudget: Money
  MaxAutoInFlight: int
  FailureBackoff: TimeSpan
  SweepConfTarget: BlockHeightOffset32
  SweepFeeRateLimit: FeeLimit
  ClientRestrictions: Restrictions
  Rules: Rules
}
  with
  static member Default = {
    AutoFeeBudget = failwith "todo"
    MaxAutoInFlight = 1
    FailureBackoff = TimeSpan.FromHours(6.)
    SweepConfTarget = 100u |> BlockHeightOffset32
    SweepFeeRateLimit = FeeRate(750m)
    ClientRestrictions = failwith "todo"
    Rules = Rules.Zero
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
        do! rule.Validate() |> Result.mapError(fun m -> $"channel %s{channel.AsString} has invalid rule {m}")
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

      do! validateRestrictions(server, this.ClientRestrictions)
      return Ok()
    }

type Config = {
  Restrictions: Swap.Category -> Task<Result<Restrictions, string>>
  Lnd: INLoopLightningClient
  LoopOutQuote: LoopOutQuoteRequest -> LoopOutQuote
  LoopInQuote: LoopInQuoteRequest -> LoopInQuote
  MinConfirmation: BlockHeightOffset32
}

type SuggestSwapError =
  | SwapDisqualified of SwapDisqualifiedReason
  | Other of string

type AutoLoopManager(logger: ILogger<AutoLoopManager>,
                     opts: IOptions<NLoopOptions>,
                     projection: SwapStateProjection,
                     boltzClient: BoltzClient,
                     lightningClientProvider: ILightningClientProvider)  =
  inherit BackgroundService()
  member val Parameters = Parameters.Default with get, set

  member private this.CheckExistingAutoLoopsIn(loopIn: LoopIn) =
    ()

  member private this.CheckExistingAutoLoopsOut(loopOuts: LoopOut seq): ExistingAutoLoopSummary =
    for o in loopOuts do
      if o.Label <> Labels.autoLoopLabel(Swap.Category.Out) then
        ()
      else
        if o.Status = SwapStatusType.InvoicePending then
          ()
      let prepay = o.Invoice
      ()
    {
      SpentFees = failwith "todo"
      PendingFees = failwith "todo"
    }

  member private this.CurrentSwapTraffic(loopOut, loopIn) =
    ()

  member private this.GetSwapRestrictions(swapType: Swap.Category, cryptoPair: inref<PairId>) = task {
      let! p = boltzClient.GetPairsAsync()
      let restrictions = p.Pairs.[PairId.toString(&cryptoPair)].Limits
      ()
    }

  member this.SuggestSwaps(category: Swap.Category, cryptoPair: inref<PairId>, autoloop: bool): Task<SwapSuggestions> = task {
    match category with
    | Swap.Category.In ->
      let loopIns = projection.OngoingLoopIns
      ()
    | Swap.Category.Out ->
      let restrictions = this.GetSwapRestrictions()
      let loopOuts = projection.OngoingLoopOuts.ToArray()
      let summary = this.CheckExistingAutoLoopsOut(loopOuts)
      if summary.TotalFees >= this.Parameters.AutoFeeBudget then
        logger.LogDebug($"autoloop fee budget: %d{this.Parameters.AutoFeeBudget.Satoshi} satoshi exhausted, "+
                        $"%d{summary.SpentFees.Satoshi} sats spent on completed swaps, %d{summary.PendingFees.Satoshi} sats reserved for ongoing swaps " +
                        "(upper limit)")
        this.SinleReasonSuggestion()
      else
        let allowedSwaps = opts.Value.MaxAutoInFlight - loopOuts.Length
        if allowedSwaps <= 0 then
          logger.LogDebug($"%d{opts.Value.MaxAutoInFlight} autoloops allowed, %d{loopOuts.Length} inflight")
          SwapDisqualifiedReason
        else
          let struct (offChainCrypto, _onChainCrypto) = cryptoPair
          let lndClient = lightningClientProvider.GetClient(offChainCrypto)
          let! channels = lndClient.ListChannels()
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

          let traffic = this.CurrentSwapTraffic(loopOuts, projection.OngoingLoopIns)

          let peersWithRules =
            peerToChannelBalance
            |> Map.toSeq
            |> Seq.choose(fun (nodeId, balance) ->
              this.Parameters.Rules.PeerRules
              |> Map.tryFind(nodeId)
              |> Option.map(fun rule -> (nodeId, balance, rule))
            )

          let mutable resp = SwapSuggestions.Zero
          let mutable suggestions = ResizeArray()
          for nodeId, balances, rule in peersWithRules do
            match! this.SuggestSwap(traffic, balances, rule, restrictions, autoloop) with
            | Error(SwapDisqualified e) ->
              resp <- { resp with DisqualifiedPeers = resp.DisqualifiedPeers |> Map.add nodeId e }
            | Error e ->
              return! Error e
            | Ok s ->
              suggestions.Add s
            ()

          for c in channels do
            let balance = Balances.FromLndResponse c
            match this.Parameters.Rules.ChannelRules.TryGetValue c.Id with
            | false, _ -> ()
            | true, rule ->
              match! this.SuggestSwap(traffic, balance, rule, restrictions, autoloop) with
              | Error(SwapDisqualified e) ->
                resp <- { resp with DisqualifiedChannels = resp.DisqualifiedChannels |> Map.add c.Id e }
              | Error e ->
                return! Error e
              | Ok s ->
                suggestions.Add s
          if suggestions.Count = 0 then
            return! Ok resp
          else
            let suggestions = suggestions |> Seq.sortBy(fun s -> s)
            ()

    return failwith "todo"
  }

  member private this.SuggestSwap(traffic, balance, rule: ThresholdRule, restrictions: Restrictions, autoloop: bool): Task<Result<SwapSuggestions, SuggestSwapError>> =
    task {
      return failwith "todo"
    }

  override this.StartAsync(ct) = unitTask {
    let rules = failwith "todo"
    this.Parameters <- { this.Parameters with Rules = rules }
    do! base.StartAsync(ct)
  }

  override this.ExecuteAsync(ct: CancellationToken) = unitTask {
    let clis: (_ * _ * _) seq =
      opts.Value.OnChainCrypto
      |> Seq.distinct
      |> Seq.map(fun x ->
        (opts.Value.GetRPCClient x, lightningClientProvider.GetClient(x), x))
    try
      while not <| ct.IsCancellationRequested do
        for rpcClient, lightningClient, cc in clis do
          let! chs = lightningClient.ListChannels()
          return failwith "todo"
    with
    | :? OperationCanceledException ->
      logger.LogInformation($"Stopping {nameof(AutoLoopManager)}...")
    | ex ->
      logger.LogError($"{ex}")
  }


