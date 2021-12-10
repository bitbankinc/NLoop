module NLoop.Server.AutoLoopHandlers

open System
open System.Threading.Tasks
open DotNetLightning.Utils
open FSharp.Control.Tasks
open Microsoft.AspNetCore.Http
open Giraffe
open Giraffe.Core
open Microsoft.Extensions.Options
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server.Actors
open NLoop.Server.RPCDTOs
open FsToolkit.ErrorHandling
open NLoop.Server.Services
open NLoop.Server.SwapServerClient

let getLiquidityParams (maybePairId: PairId option) : HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    let man = ctx.GetService<AutoLoopManager>()
    let pairId =
      maybePairId
      |> Option.defaultValue PairId.Default

    let group = {
      /// todo: consider loop in
      Swap.Group.Category = Swap.Category.Out
      Swap.Group.PairId = pairId
    }
    let p = man.Parameters.[group]
    let resp = {
      LiquidityParameters.Rules = [||]
      FeePPM = ValueNone
      SweepFeeRateSatPerKVByte = ValueNone
      MaxSwapFeePpm = ValueNone
      MaxRoutingFeePpm = ValueNone
      MaxPrepayRoutingFeePpm = ValueNone
      MaxPrepay = ValueNone
      MaxMinerFee = ValueNone
      SweepConfTarget = p.SweepConfTarget.Value |> int
      FailureBackoffSecond = p.FailureBackoff.TotalSeconds |> int
      AutoLoop = p.AutoLoop
      AutoMaxInFlight = p.MaxAutoInFlight
      MinSwapAmount = p.ClientRestrictions.Minimum
      MaxSwapAmount = p.ClientRestrictions.Maximum
    }
    let resp =
      match p.FeeLimit with
      | :? FeePortion as f ->
        {
          resp
            with
              FeePPM = f.PartsPerMillion |> ValueSome
        }
      | :? FeeCategoryLimit as f ->
        {
          resp
            with
            SweepFeeRateSatPerKVByte = f.SweepFeeRateLimit.FeePerK |> ValueSome
            MaxMinerFee = f.MaximumMinerFee |> ValueSome
            MaxSwapFeePpm = f.MaximumSwapFeePPM |> ValueSome
            MaxRoutingFeePpm = f.MaximumRoutingFeePPM |> ValueSome
            MaxPrepayRoutingFeePpm = f.MaximumPrepayRoutingFeePPM |> ValueSome
            MaxPrepay = f.MaximumPrepay |> ValueSome
        }
      | x -> failwith $"unknown type of FeeLimit {x}"
    return! json resp next ctx
  }

let private dtoToFeeLimit (pairId: PairId) (r: LiquidityParameters): Result<IFeeLimit, _> =
  let isFeePPM = r.FeePPM.IsSome
  let isCategories =
    r.MaxSwapFeePpm.IsSome  ||
    r.MaxRoutingFeePpm.IsSome ||
    r.MaxPrepayRoutingFeePpm.IsSome ||
    r.MaxMinerFee.IsSome ||
    r.MaxPrepay.IsSome ||
    r.SweepFeeRateSatPerKVByte.IsSome
  if isFeePPM && isCategories then
    Error "set either fee ppm, or individual fee categories"
  elif isFeePPM then
    { FeePortion.PartsPerMillion = r.FeePPM.Value }
    :> IFeeLimit
    |> Ok
  elif isCategories then
    let p = pairId.DefaultLoopOutParameters
    {
      FeeCategoryLimit.MaximumSwapFeePPM =
        r.MaxSwapFeePpm
        |> ValueOption.defaultValue(p.MaxSwapFeePPM)
      MaximumPrepay =
        r.MaxPrepay
        |> ValueOption.defaultValue(p.MaxPrepay)
      MaximumRoutingFeePPM =
        r.MaxRoutingFeePpm
        |> ValueOption.defaultValue(defaultMaxRoutingFeePPM)
      MaximumPrepayRoutingFeePPM =
        r.MaxPrepayRoutingFeePpm
        |> ValueOption.defaultValue defaultMaxPrepayRoutingFeePPM
      MaximumMinerFee =
        r.MaxMinerFee
        |> ValueOption.defaultValue(p.MaxMinerFee)
      SweepFeeRateLimit =
        r.SweepFeeRateSatPerKVByte
        |> ValueOption.map FeeRate
        |> ValueOption.defaultValue(p.SweepFeeRateLimit)
    }
    :> IFeeLimit
    |> Ok
  else
    Error "no fee categories set"

let setLiquidityParams (maybePairId: PairId option) ({ SetLiquidityParametersRequest.Parameters = req }): HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    let man = ctx.GetService<AutoLoopManager>()
    let pairId =
      maybePairId
      |> Option.defaultValue PairId.Default

    let group = {
      /// todo: consider loop in
      Swap.Group.Category = Swap.Category.Out
      Swap.Group.PairId = pairId
    }

    match dtoToFeeLimit pairId req with
    | Error e ->
      return!
        validationError400 [e] next ctx
    | Ok feeLimit ->
      let p = {
        Parameters.Rules =
          Rules.FromDTOs(req.Rules)
        MaxAutoInFlight =
          req.AutoMaxInFlight
        FailureBackoff =
          req.FailureBackoffSecond |> float |> TimeSpan.FromSeconds
        SweepConfTarget =
          req.SweepConfTarget |> uint |> BlockHeightOffset32
        FeeLimit = feeLimit
        ClientRestrictions = {
          Minimum = req.MinSwapAmount
          Maximum = req.MaxSwapAmount
        }
        AutoLoop = req.AutoLoop
      }
      match! man.SetParameters(group, p) with
      | Ok () ->
        return! json {||} next ctx
      | Error e ->
        return!
          validationError400 [e.Message] next ctx
  }

let suggestSwaps (maybePairId: PairId option): HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    let man = ctx.GetService<AutoLoopManager>()
    let pairId =
      maybePairId
      |> Option.defaultValue PairId.Default
    // todo: consider loopin
    let  group = {
      Swap.Group.Category = Swap.Category.Out
      Swap.Group.PairId = pairId
    }
    match! man.SuggestSwaps(false, group) with
    | Error e ->
      return! error503 e next ctx
    | Ok suggestion ->
      let d =
        let channelD =
          suggestion.DisqualifiedChannels
          |> Seq.map(fun kv -> {
            Disqualified.Reason = kv.Value.Message
            ChannelId = kv.Key |> ValueSome
            PubKey = ValueNone })
        let peerD =
          suggestion.DisqualifiedPeers
          |> Seq.map(fun kv -> {
            Disqualified.Reason = kv.Value.Message
            ChannelId = ValueNone
            PubKey = kv.Key.Value |> ValueSome })
        seq [ channelD; peerD ]
        |> Seq.concat
        |> Seq.toArray
      let resp = {
        SuggestSwapsResponse.Disqualified = d
        LoopOut =
          suggestion.OutSwaps |> List.toArray
        LoopIn =
          suggestion.InSwaps |> List.toArray
      }
      return! json resp next ctx
  }
