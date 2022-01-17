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
open NLoop.Server.Options
open NLoop.Server.Services
open NLoop.Server.SwapServerClient
let getLiquidityParams (offChainAsset: SupportedCryptoCode) : HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    match ctx.GetService<TryGetAutoLoopManager>() offChainAsset with
    | None -> return! errorBadRequest [$"off chain asset {offChainAsset} not supported"] next ctx
    | Some man ->
    match man.Parameters with
    | None -> return! errorBadRequest [$"no parameter for {offChainAsset} has been set yet."] next ctx
    | Some p ->
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
      HTLCConfTarget = p.HTLCConfTarget.Value |> int |> Some
      MinSwapAmountLoopOut = p.ClientRestrictions.OutMinimum
      MaxSwapAmountLoopOut = p.ClientRestrictions.OutMaximum
      MinSwapAmountLoopIn = p.ClientRestrictions.InMinimum
      MaxSwapAmountLoopIn = p.ClientRestrictions.InMaximum
      OnChainAsset = p.OnChainAsset |> ValueSome
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

let private dtoToFeeLimit
  (offChain: CryptoCodeDefaultOffChainParams, onChain: CryptoCodeDefaultOnChainParams)
  (r: LiquidityParameters): Result<IFeeLimit, _> =
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
    {
      FeeCategoryLimit.MaximumSwapFeePPM =
        r.MaxSwapFeePpm
        |> ValueOption.defaultValue(offChain.MaxSwapFeePPM)
      MaximumPrepay =
        r.MaxPrepay
        |> ValueOption.defaultValue(offChain.MaxPrepay)
      MaximumRoutingFeePPM =
        r.MaxRoutingFeePpm
        |> ValueOption.defaultValue(defaultMaxRoutingFeePPM)
      MaximumPrepayRoutingFeePPM =
        r.MaxPrepayRoutingFeePpm
        |> ValueOption.defaultValue defaultMaxPrepayRoutingFeePPM
      MaximumMinerFee =
        r.MaxMinerFee
        |> ValueOption.defaultValue(onChain.MaxMinerFee)
      SweepFeeRateLimit =
        r.SweepFeeRateSatPerKVByte
        |> ValueOption.map FeeRate
        |> ValueOption.defaultValue(onChain.SweepFeeRateLimit)
    }
    :> IFeeLimit
    |> Ok
  else
    Error "no fee categories set"

let setLiquidityParams
  (maybeOffChainAsset: SupportedCryptoCode option)
  { SetLiquidityParametersRequest.Parameters = req }: HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    let offchainAsset = defaultArg maybeOffChainAsset SupportedCryptoCode.BTC
    let onChainAsset = req.OnChainAsset |> ValueOption.defaultValue SupportedCryptoCode.BTC
    match ctx.GetService<TryGetAutoLoopManager>()(offchainAsset) with
    | None -> return! errorBadRequest[$"No AutoLoopManager for offchain asset {offchainAsset}"] next ctx
    | Some man ->
    match dtoToFeeLimit (offchainAsset.DefaultParams.OffChain, onChainAsset.DefaultParams.OnChain) req with
    | Error e ->
      return!
        errorBadRequest [e] next ctx
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
          OutMinimum = req.MinSwapAmountLoopOut
          OutMaximum = req.MaxSwapAmountLoopOut
          InMinimum = req.MinSwapAmountLoopIn
          InMaximum = req.MaxSwapAmountLoopIn
        }
        HTLCConfTarget =
          req.HTLCConfTarget
          |> Option.map(uint >> BlockHeightOffset32)
          |> Option.defaultValue onChainAsset.DefaultParams.OnChain.HTLCConfTarget
        AutoLoop = req.AutoLoop
        OnChainAsset =
          onChainAsset
      }
      match! man.SetParameters p with
      | Ok () ->
        return! json {||} next ctx
      | Error e ->
        return!
          errorBadRequest [e.Message] next ctx
  }

let suggestSwaps (maybeOffchainAsset: SupportedCryptoCode option): HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    let offchainAsset = defaultArg maybeOffchainAsset SupportedCryptoCode.BTC
    match ctx.GetService<TryGetAutoLoopManager>()(offchainAsset) with
    | None -> return! errorBadRequest[$"No AutoLoopManager for offchain asset {offchainAsset}"] next ctx
    | Some man ->
    match! man.SuggestSwaps false with
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
