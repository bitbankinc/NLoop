namespace NLoop.Server

open System
open System.Runtime.InteropServices
open Microsoft.Extensions.Options
open NLoop.Server.RPCDTOs
open Newtonsoft.Json
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open FsToolkit.ErrorHandling
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server.Actors
open NLoop.Server.Options
open NLoop.Server.DTOs
open NLoop.Server.Services
open StreamJsonRpc

type D = DefaultParameterValueAttribute

type LightningInitConfiguration = {
  [<JsonPropertyName "lightning-dir">]
  LightningDir: string
}

type INLoopJsonRpcServer =
  abstract member GetLiquidityParams: offChainAsset: NLoopClient.CryptoCode -> Task<NLoopClient.LiquidityParameters>
  abstract member Info: unit -> Task<NLoopClient.GetInfoResponse>
  abstract member Version: unit -> Task<string>
  abstract member SetLiquidityParams: liquidityParameters: NLoopClient.SetLiquidityParametersRequest * offChainAsset: NLoopClient.CryptoCode -> Task

  abstract member LoopOut: request: NLoopClient.LoopOutRequest -> Task<NLoopClient.LoopOutResponse>
  abstract member LoopIn: request: NLoopClient.LoopInRequest -> Task<NLoopClient.LoopInResponse>

/// Sadly, StreamJsonRpc does not support System.Text.Json natively.
/// So we first take openapi-generated DTO as an argument, which is expected to be serialized with Newtonsoft.Json
/// and convert it with SystemTextJson.
/// The compatibility of two forms of DTO is assured by NLoop.Server.PropertyTests
type NLoopJsonRpcServer(blockListener: IBlockChainListener,
                        swapExecutor: ISwapExecutor,
                        tryGetAutoLoopManager: TryGetAutoLoopManager) =

  [<JsonRpcMethod("loopout")>]
  member this.LoopOut(req: NLoopClient.LoopOutRequest): Task<NLoopClient.LoopOutResponse> =
    task {
      let req: LoopOutRequest = convertDTOToNLoopCompatibleStyle req
      let height = blockListener.CurrentHeight req.PairIdValue.Base
      match! swapExecutor.ExecNewLoopOut(req, height) with
      | Ok response ->
        return response |> convertDTOToJsonRPCStyle
      | Error e ->
        return raise <| Exception e
    }

  [<JsonRpcMethod("loopin")>]
  member this.LoopIn(_req: NLoopClient.LoopInRequest) : Task<NLoopClient.LoopInResponse> =
    task {
      return failwith "todo"
    }

  [<JsonRpcMethod("init")>]
  member this.Init(_configuration) =
    task {
      return failwith "todo"
    }

  [<JsonRpcMethod("swaphistory")>]
  member this.SwapHistory() =
    ()

  [<JsonRpcMethod("ongoingswaps")>]
  member this.OngoingSwaps() =
    ()

  [<JsonRpcMethod("swapcostsummary")>]
  member this.SwapCostSummary() =
    ()

  [<JsonRpcMethod("suggestswaps")>]
  member this.SuggestSwaps() =
    ()

  [<JsonRpcMethod("version")>]
  member this.Version() =
    task {
      return Constants.AssemblyVersion
    }
  [<JsonRpcMethod("set_liquidityparams")>]
  member this.SetLiquidityParams(liquidityParameters: NLoopClient.SetLiquidityParametersRequest, [<O;D(NLoopClient.CryptoCode.BTC)>]offchainAsset: NLoopClient.CryptoCode) =
    task {
      let { Parameters = req }: SetLiquidityParametersRequest  = convertDTOToNLoopCompatibleStyle liquidityParameters
      let onChainAsset = req.OnChainAsset |> ValueOption.defaultValue SupportedCryptoCode.BTC
      let offchainAsset: SupportedCryptoCode = convertDTOToNLoopCompatibleStyle offchainAsset
      match tryGetAutoLoopManager(offchainAsset) with
      | None ->
        raise <| Exception ($"No AutoLoopManager for offchain asset {offchainAsset}")
      | Some man ->
      match req.Rules |> Seq.map(fun r -> r.Validate()) |> Seq.toList |> List.sequenceResultA with
      | Error errs ->
        raise <| Exception (errs.ToString())
      | Ok _ ->
      match AutoLoopHandlers.dtoToFeeLimit (offchainAsset.DefaultParams.OffChain, onChainAsset.DefaultParams.OnChain) req with
      | Error e ->
        raise <| exn e
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
          if req.AutoMaxInFlight > 2 then
            let msg = "autoloop is experimental, usually it is not good idea to set auto_max_inflight larger than 2"
            raise <| exn msg
          else
            return ()
        | Error e ->
          return raise <| exn (e.ToString())
    } :> Task

  [<JsonRpcMethod("get_liquidityparams")>]
  member this.GetLiquidityParams(offChainAsset: NLoopClient.CryptoCode) =
    task {
      let offChainAsset = convertDTOToNLoopCompatibleStyle offChainAsset
      match tryGetAutoLoopManager offChainAsset with
      | None ->
        return raise <| Exception $"off chain asset {offChainAsset} not supported"
      | Some man ->
      match man.Parameters with
      | None ->
        return raise <| exn $"no parameter for {offChainAsset} has been set yet."
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
      return convertDTOToJsonRPCStyle resp
    }

  interface INLoopJsonRpcServer with

    [<JsonRpcMethod("info")>]
    member this.Info(): Task<NLoopClient.GetInfoResponse> =
      task {
        let response = {
          GetInfoResponse.Version = Constants.AssemblyVersion
          SupportedCoins = { OnChain = [SupportedCryptoCode.BTC; SupportedCryptoCode.LTC]
                             OffChain = [SupportedCryptoCode.BTC] }
        }
        return response |> convertDTOToJsonRPCStyle
      }

    member this.GetLiquidityParams(offChainAsset) =
      this.GetLiquidityParams(offChainAsset)
    member this.SetLiquidityParams(parameters, offChainAsset) =
      this.SetLiquidityParams(parameters, offChainAsset)
    member this.LoopIn(request) = this.LoopIn(request)
    member this.LoopOut(request) = this.LoopOut(request)
    member this.Version() = Constants.AssemblyVersion |> Task.FromResult
