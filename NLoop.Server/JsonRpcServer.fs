namespace NLoop.Server

open System
open System.Collections.Generic
open System.Reflection
open System.Runtime.InteropServices
open DotNetLightning.Serialization
open Microsoft.VisualStudio.Threading
open NLoop.Server.RPCDTOs
open System.Threading.Tasks
open DotNetLightning.Utils
open FsToolkit.ErrorHandling
open NLoop.Domain
open NLoop.Server.Actors
open NLoop.Server.Options
open NLoop.Server.DTOs
open NLoop.Server.Services
open StreamJsonRpc

type D = DefaultParameterValueAttribute

[<NoComparison;NoEquality;CLIMutable>]
type RPCMethod = {
   [<Newtonsoft.Json.JsonProperty "name">]
   Name: string
   [<Newtonsoft.Json.JsonProperty "usage">]
   Usage: string
   [<Newtonsoft.Json.JsonProperty "description">]
   Description: string
   [<Newtonsoft.Json.JsonProperty "long_description">]
   LongDescription: string
}


[<NoComparison;NoEquality;CLIMutable>]
type PluginOptionsDTO = {
  [<Newtonsoft.Json.JsonProperty "name">]
  Name: string
  [<Newtonsoft.Json.JsonProperty "default">]
  Default: obj
  [<Newtonsoft.Json.JsonProperty "description">]
  Description: string
  [<Newtonsoft.Json.JsonProperty "type">]
  OptType: string
  [<Newtonsoft.Json.JsonProperty "deprecated">]
  Deprecated: bool
}
  with
  static member FromRootCLIOption(op: System.CommandLine.Option)  =
    {
      Name = op.Name
      Default = if op.Argument.HasDefaultValue then op.Argument.GetDefaultValue() else obj()
      Description = op.Description
      OptType = op.Argument.ArgumentType.ToString()
      Deprecated = false
    }

[<CLIMutable>]
type FeatureSetDTO = {
  init: string
  node: string
  channel: string
  invoice: string
}

[<CLIMutable>]
type NotificationsDTO = {
  [<Newtonsoft.Json.JsonProperty "method">]
  Method: string
}

[<CLIMutable>]
type Manifest = {
  [<Newtonsoft.Json.JsonProperty "options">]
  Options: PluginOptionsDTO seq
  [<Newtonsoft.Json.JsonProperty "rpcmethods">]
  RPCMethods: RPCMethod seq
  [<Newtonsoft.Json.JsonProperty "subscriptions">]
  Subscriptions: string seq
  [<Newtonsoft.Json.JsonProperty "hooks">]
  Hooks: obj seq
  [<Newtonsoft.Json.JsonProperty "dynamic">]
  Dynamic: bool
  [<Newtonsoft.Json.JsonProperty "notifications">]
  Notifications: NotificationsDTO seq
  [<Newtonsoft.Json.JsonProperty "featurebits">]
  FeatureBits: FeatureSetDTO
}

[<CLIMutable>]
type ProxyDTO = {
  [<Newtonsoft.Json.JsonProperty "type">]
  ty: string
  address: string
  port: int
}

[<CLIMutable>]
type LightningInitConfigurationDTO = {
  [<Newtonsoft.Json.JsonProperty "lightning-dir">]
  LightningDir: string
  [<Newtonsoft.Json.JsonProperty "rpc-file">]
  RpcFile: string

  [<Newtonsoft.Json.JsonProperty "startup">]
  Startup: bool

  [<Newtonsoft.Json.JsonProperty "network">]
  Network: string

  [<Newtonsoft.Json.JsonProperty "feature_set">]
  FeatureSet: FeatureSetDTO

  [<Newtonsoft.Json.JsonProperty "proxy">]
  Proxy: ProxyDTO

  [<Newtonsoft.Json.JsonProperty "torv3-enabled">]
  TorV3Enabled: bool

  [<Newtonsoft.Json.JsonProperty "always_use_proxy">]
  AlwaysUseProxy: bool
}

type INLoopJsonRpcServer =
  abstract member GetLiquidityParams: offChainAsset: NLoopClient.CryptoCode -> Task<NLoopClient.LiquidityParameters>
  abstract member Info: unit -> Task<NLoopClient.GetInfoResponse>
  abstract member Version: unit -> Task<string>
  abstract member SetLiquidityParams: liquidityParameters: NLoopClient.SetLiquidityParametersRequest * offChainAsset: NLoopClient.CryptoCode -> Task

  abstract member LoopOut: request: NLoopClient.LoopOutRequest -> Task<NLoopClient.LoopOutResponse>
  abstract member LoopIn: request: NLoopClient.LoopInRequest -> Task<NLoopClient.LoopInResponse>
  abstract member Init: configuration: LightningInitConfigurationDTO * options: Dictionary<string, obj> -> Task<obj>
  abstract member GetManifest: unit -> Task<Manifest>


/// json-rpc 2.0 server for StreamJsonRpc.
/// This is necessary for NLoop to work as a clightning-plugin.
/// Sadly, StreamJsonRpc does not support System.Text.Json natively.
/// So we first take openapi-generated DTO as an argument, which is expected to be serialized with Newtonsoft.Json
/// and convert it with SystemTextJson.
/// The compatibility of two forms of DTO is assured by NLoop.Server.PropertyTests
///
/// Since the communication to lightningd is primarily through stdin/out, we
/// restrict its max concurrent execution to be 1 by using AsyncSemaphore. So that it
/// won't corrupt the communication channel.
type NLoopJsonRpcServer(blockListener: IBlockChainListener,
                        swapExecutor: ISwapExecutor,
                        pluginSettings: PluginServerSettings,
                        tryGetAutoLoopManager: TryGetAutoLoopManager) =
  let semaphore = new AsyncSemaphore(1)

  let RpcDescriptions =
    Map.ofSeq[
      ("info", "Get basic information about nloop.")
      ("swaphistory", "Get the full history of swaps. This might take long if you have a lots of entries in a database.")
      ("ongoingswaps", "Get the list of ongoing swaps.")
      ("swapcostsummary", "Get the summary of the cost we paid for swaps.")
      ("loopout", "initiate loopout.")
      ("loopin", "initiate loopin.")
      ("suggestswaps", "Get suggestion for the swaps. You must set liquidity parameters for autoloop before "
                       + "getting the suggestion, this endpoint is usually useful when you set `autoloop=false` "
                       + "to liquidity params. (that is, autoloop dry-run mode.). see `set_liquidityparams`")
      ("get_liquidityparams", "Get the parameters that the daemon's liquidity manager is currently configured with. "
                              + "This may be nil if nothing is configured.")
      ("set_liquidityparams", "Overwrites the current set of parameters for the daemon's liquidity manager.")
    ]

  member val IsInitiated = false with get, set

  [<JsonRpcMethod("init")>]
  member this.Init(configuration: LightningInitConfigurationDTO, options: Dictionary<string, obj>): Task<obj> =
    task {
      use! _releaser = semaphore.EnterAsync()
      let disabledReason =
        if pluginSettings.Opts.Network <> configuration.Network then
          Some $"Network mismatch"
        else
          None
      match disabledReason with
      | Some s ->
        return s |> box
      | _ ->
        for op in options do
          let name = op.Key
          match pluginSettings.Opts.GetType().GetProperties() |> Seq.tryFind(fun p -> p.Name.ToLowerInvariant() = name.ToLowerInvariant()) with
          | Some p ->
            p.SetValue(pluginSettings.Opts, op.Value)
          | None ->
            ()
        pluginSettings.IsInitiated <- true
        return () |> box
    }

  [<JsonRpcMethod("getmanifest")>]
  member this.GetManifest(): Task<Manifest> =
    task {
      use! _releaser = semaphore.EnterAsync()

      let userDefinedMethodInfo =
        let equalStr a b = String.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        this.GetType().GetMethods(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly)
        |> Seq.filter(fun m -> not <| m.IsSpecialName && not <| (equalStr "init" m.Name) && not <| (equalStr "getmanifest" m.Name))
      let methods =
        userDefinedMethodInfo
        |> Seq.map(fun m ->
          let name = m.Name.ToLowerInvariant()
          let argSpec =
            m.GetParameters()
          let numDefaults =
            argSpec
            |> Seq.filter(fun s -> s.HasDefaultValue)
            |> Seq.length
          let keywordArgsStartIndex = argSpec.Length - numDefaults
          let args =
            argSpec
            |> Seq.filter(fun s ->
              let comp v = not <| String.Equals(s.Name, v, StringComparison.OrdinalIgnoreCase)
              comp "plugin" && comp "request"
              )
            |> Seq.mapi(fun i s ->
                if i < keywordArgsStartIndex then
                  // positional arguments
                  s.Name
                else
                  // keyword arguments
                  $"[{s.Name}]"
              )
          {
            Name = name
            Usage = String.Join(' ', args)
            Description = RpcDescriptions.[name]
            LongDescription = RpcDescriptions.[name] + $": see {Constants.ApiDocUrl} for more details."
          }
        )

      return {
        Options =
          NLoopServerCommandLine.getOptions() |> Seq.map(PluginOptionsDTO.FromRootCLIOption)
        RPCMethods = methods
        Notifications =
          Swap.AllTagEvents
          |> Seq.map(fun s -> { Method = s })
        Subscriptions = []
        Hooks = []
        Dynamic = true
        FeatureBits = Unchecked.defaultof<_>
      }
    }

  [<JsonRpcMethod("loopout")>]
  member this.LoopOut(req: NLoopClient.LoopOutRequest): Task<NLoopClient.LoopOutResponse> =
    task {
      use! _releaser = semaphore.EnterAsync()
      let req: LoopOutRequest = convertDTOToNLoopCompatibleStyle req
      let height = blockListener.CurrentHeight req.PairIdValue.Base
      match! swapExecutor.ExecNewLoopOut(req, height) with
      | Ok response ->
        return response |> convertDTOToJsonRPCStyle
      | Error e ->
        return raise <| Exception e
    }

  [<JsonRpcMethod("loopin")>]
  member this.LoopIn(req: NLoopClient.LoopInRequest) : Task<NLoopClient.LoopInResponse> =
    task {
      use! _releaser = semaphore.EnterAsync()
      let req: LoopInRequest = convertDTOToNLoopCompatibleStyle req
      let height =
        blockListener.CurrentHeight req.PairIdValue.Quote
      match! swapExecutor.ExecNewLoopIn(req, height) with
      | Ok resp ->
        return resp |> convertDTOToJsonRPCStyle
      | Error e ->
        return raise <| exn e
    }


  [<JsonRpcMethod("swaphistory")>]
  member this.SwapHistory() =
    task {
      use! _releaser = semaphore.EnterAsync()
      return ()
    }

  [<JsonRpcMethod("ongoingswaps")>]
  member this.OngoingSwaps() =
    task {
      use! _releaser = semaphore.EnterAsync()
      return ()
    }

  [<JsonRpcMethod("swapcostsummary")>]
  member this.SwapCostSummary() =
    task {
      use! _releaser = semaphore.EnterAsync()
      return ()
    }

  [<JsonRpcMethod("suggestswaps")>]
  member this.SuggestSwaps() =
    task {
      use! _releaser = semaphore.EnterAsync()
      return ()
    }
  [<JsonRpcMethod("set_liquidityparams")>]
  member this.Set_LiquidityParams(liquidityParameters: NLoopClient.SetLiquidityParametersRequest, [<O;D(NLoopClient.CryptoCode.BTC)>]offchainAsset: NLoopClient.CryptoCode) =
    task {
      use! _releaser = semaphore.EnterAsync()
      let { Parameters = req }: SetLiquidityParametersRequest  = convertDTOToNLoopCompatibleStyle liquidityParameters
      let onChainAsset = req.OnChainAsset |> ValueOption.defaultValue SupportedCryptoCode.BTC
      let offchainAsset: SupportedCryptoCode = convertDTOToNLoopCompatibleStyle offchainAsset
      match tryGetAutoLoopManager(offchainAsset) with
      | None ->
        raise <| Exception $"No AutoLoopManager for offchain asset {offchainAsset}"
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
  member this.Get_LiquidityParams(offChainAsset: NLoopClient.CryptoCode) =
    task {
      use! _releaser = semaphore.EnterAsync()
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
        use! _releaser = semaphore.EnterAsync()
        let response = {
          GetInfoResponse.Version = Constants.AssemblyVersion
          SupportedCoins = { OnChain = [SupportedCryptoCode.BTC; SupportedCryptoCode.LTC]
                             OffChain = [SupportedCryptoCode.BTC] }
        }
        return response |> convertDTOToJsonRPCStyle
      }

    member this.GetLiquidityParams(offChainAsset) =
      this.Get_LiquidityParams(offChainAsset)
    member this.SetLiquidityParams(parameters, offChainAsset) =
      this.Set_LiquidityParams(parameters, offChainAsset)
    member this.LoopIn(request) = this.LoopIn(request)
    member this.LoopOut(request) = this.LoopOut(request)
    member this.Version() = Constants.AssemblyVersion |> Task.FromResult
    [<JsonRpcMethod("getmanifest")>]
    member this.GetManifest() = this.GetManifest()
    member this.Init(configuration, options) = this.Init(configuration, options)

  interface IDisposable with
    member this.Dispose() =
      semaphore.Dispose()
