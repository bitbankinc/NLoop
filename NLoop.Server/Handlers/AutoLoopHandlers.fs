module NLoop.Server.Handlers.AutoLoopHandlers

open System
open DotNetLightning.Utils.Primitives
open FsToolkit.ErrorHandling
open NBitcoin
open NLoop.Domain

open NLoop.Server
open NLoop.Server.Handlers
open NLoop.Server.Options
open NLoop.Server.RPCDTOs
open NLoop.Server.Services


let handleGetLiquidityParams
  (tryGetAutoLoopManager: TryGetAutoLoopManager)
  offChainAsset
  =
  task {
    match tryGetAutoLoopManager offChainAsset with
    | None ->
      return
        [$"off chain asset {offChainAsset} not supported"]
        |> HandlerError.InvalidRequest |> Error
    | Some man ->
    match man.Parameters with
    | None ->
      return
        [$"no parameter for {offChainAsset} has been set yet."]
        |> HandlerError.InvalidRequest |> Error
    | Some p ->
    let resp = {
      LiquidityParameters.Rules = p.Rules.ToDTO()
      FeePPM =
        ValueNone
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
    return Ok resp
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

let setLiquidityParamsCore
  (tryGetAutoLoopManager: TryGetAutoLoopManager)
  (offchainAsset: SupportedCryptoCode)
  { SetLiquidityParametersRequest.Parameters = req } =
  task {
    let onChainAsset = req.OnChainAsset |> ValueOption.defaultValue SupportedCryptoCode.BTC
    match tryGetAutoLoopManager offchainAsset with
    | None ->
      return
        [$"No AutoLoopManager for offchain asset {offchainAsset}"]
        |> HandlerError.InvalidRequest
        |> Error
    | Some man ->
    match req.Rules |> Seq.map(fun r -> r.Validate()) |> Seq.toList |> List.sequenceResultA with
    | Error errs ->
      return
        errs
        |> HandlerError.InvalidRequest
        |> Error
    | Ok _ ->
    match dtoToFeeLimit (offchainAsset.DefaultParams.OffChain, onChainAsset.DefaultParams.OnChain) req with
    | Error e ->
      return
        [e]
        |> HandlerError.InvalidRequest
        |> Error
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
      return!
        man.SetParameters p
        |> TaskResult.mapError(fun e -> [e.Message] |> HandlerError.InvalidRequest)
  }
let handleSetLiquidityParams
  lnClientProvider
  tryGetAutoLoopManager
  (req: SetLiquidityParametersRequest)
  (maybeOffChainAsset: SupportedCryptoCode option)
  =
  taskResult {
    let offchainAsset = defaultArg maybeOffChainAsset SupportedCryptoCode.BTC
    let targets =
      req.Parameters.Targets
    do! checkWeHaveChannel offchainAsset targets.Channels lnClientProvider
    return! setLiquidityParamsCore tryGetAutoLoopManager offchainAsset req
  }

let handleSuggestSwaps
  (tryGetAutoLoopManager: TryGetAutoLoopManager)
  (maybeOffchainAsset: SupportedCryptoCode option) =
  task {
    let offchainAsset = defaultArg maybeOffchainAsset SupportedCryptoCode.BTC
    match tryGetAutoLoopManager(offchainAsset) with
    | None ->
      return
        [$"No AutoLoopManager for offchain asset {offchainAsset}"]
        |> HandlerError.InvalidRequest
        |> Error
    | Some man ->
    match! man.SuggestSwaps false with
    | Error e ->
      return
        e.Message |> HandlerError.InternalError |> Error
    | Ok suggestion ->
      return
        {
          SuggestSwapsResponse.Disqualified =
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
          LoopOut =
            suggestion.OutSwaps |> List.toArray
          LoopIn =
            suggestion.InSwaps |> List.toArray
        }
        |> Ok
  }
