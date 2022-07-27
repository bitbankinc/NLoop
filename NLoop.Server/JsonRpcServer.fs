namespace NLoop.Server

open System
open System.Collections.Generic
open System.CommandLine
open System.IO
open System.IO.Pipelines
open System.Runtime.InteropServices
open System.Threading.Tasks
open DotNetLightning.ClnRpc
open DotNetLightning.Utils.Primitives
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain.Utils
open NLoop.Server.Actors
open NLoop.Server.DTOs
open NLoop.Server.Options
open NLoop.Server.RPCDTOs
open NLoop.Server.Services
open Newtonsoft.Json
open System.Text.Json
open DotNetLightning.ClnRpc.Plugin
open NLoop.Domain
open NLoop.Domain.IO
open FSharp.Control.Reactive
open StreamJsonRpc

// "state machine is not statically compilable" error.
// The performance does not really matters on the top level of the json-rpc. So ignore.
#nowarn "3511"

[<AutoOpen>]
module private JsonRpcServerHelpers =
  let inline convertDTOToNLoopCompatibleStyle(input: 'TIn) =
    let json =
      let deserializeSettings = JsonSerializerSettings(NullValueHandling = NullValueHandling.Include, MissingMemberHandling = MissingMemberHandling.Error)
      JsonConvert.SerializeObject(input, deserializeSettings)
    let opts = JsonSerializerOptions(IgnoreNullValues = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    opts.AddNLoopJsonConverters()
    JsonSerializer.Deserialize<'T>(json, opts)

  let inline convertDTOToJsonRPCStyle (input: 'TIn) : 'T =
    let json =
      let opts = JsonSerializerOptions(IgnoreNullValues = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
      opts.AddNLoopJsonConverters()
      JsonSerializer.Serialize<'TIn>(input, opts)
    let deserializeSettings = JsonSerializerSettings(NullValueHandling = NullValueHandling.Include, MissingMemberHandling = MissingMemberHandling.Error)
    JsonConvert.DeserializeObject<'T>(json, deserializeSettings)


module PluginOptions =

  [<Literal>]
  let opPrefix = "nloop-"
  let addPrefix n = $"{opPrefix}{n}"
  let removePrefix (n: string) =
    let n = if n.StartsWith "--" then n.TrimStart("--".ToCharArray()) else n
    n.TrimStart(opPrefix.ToCharArray())

  let fromRootCLIOption(op: System.CommandLine.Option) =
    let ty =
      if op.Argument.ArgumentType.IsGenericType then
        op.Argument.ArgumentType.GetGenericArguments().[0]
      else
        op.Argument.ArgumentType
    {
      Name = addPrefix op.Name
      Default =
        if op.Argument.HasDefaultValue then op.Argument.GetDefaultValue() else
        if ty.IsValueType then Activator.CreateInstance(ty) else null
      Description =
        op.Description
      OptionType =
        if ty = typeof<int> || ty = typeof<int[]> then
          PluginOptType.Int
        else if
          ty = typeof<string>
          || ty = typeof<string[]>
          || ty = typeof<FileInfo>
          || ty = typeof<FileInfo[]>
          || ty = typeof<DirectoryInfo>
          || ty = typeof<DirectoryInfo[]>
          || ty = typeof<SupportedCryptoCode>
          || ty = typeof<SupportedCryptoCode[]>
          then
            PluginOptType.String
        else if
          ty = typeof<bool>
          || ty = typeof<bool[]>
          then
            PluginOptType.Bool
        else if op.Argument.Arity = ArgumentArity.Zero then
          PluginOptType.Flag
        else
          failwith $"Unsupported commandline argument type for c-lightning: {ty}"
      Multi =
        op.Argument.Arity.MaximumNumberOfValues > 1
      Deprecated = false
    }


type NLoopJsonRpcServer
  (
    blockListener: IBlockChainListener,
    swapExecutor: ISwapExecutor,
    eventAggregator: IEventAggregator,
    loggerFactory: ILoggerFactory,
    tryGetAutoLoopManager: TryGetAutoLoopManager,
    applicationLifetime: IHostApplicationLifetime,
    optionsHolder: NLoopOptionsHolder
  ) as this =
  inherit PluginServerBase(Swap.AllTagEvents, false, loggerFactory.CreateLogger<PluginServerBase>().LogDebug)
  let logger: ILogger<NLoopJsonRpcServer> = loggerFactory.CreateLogger<_>()
  

  let _subscription =
    eventAggregator.GetObservable<RecordedEvent<Swap.Event>>()
    |> Observable.flatmapTask(fun re ->
      backgroundTask {
        try
          use! _releaser = this.AsyncSemaphore.EnterAsync()
          do! this.SendNotification(re.Data.Type, [|re|])
        with
        | ex ->
          logger.LogError(ex, "Failed to send CLightning notification")
      })
    |> Observable.subscribe id
  do
    logger.LogDebug $"NLoopJsonRpcServer Initialized"

  override this.Options =
    NLoopServerCommandLine.getOptions() |> Seq.map(PluginOptions.fromRootCLIOption)

  member val NLoopOptions = NLoopOptions()

  member private this.SetArrayedProperty<'T>(p: Reflection.PropertyInfo, values, ?next) =
    try
      p.SetValue(this.NLoopOptions, values |> Seq.cast<'T> |> Seq.toArray)
    with
    | :? InvalidCastException when next.IsSome ->
      next.Value()
      
  override this.InitCore(_configuration, options) =

    let optsProperties =
      this.NLoopOptions.GetType().GetProperties()
      |> Seq.map(fun p -> p.Name.ToLowerInvariant(), p)
      |> Map.ofSeq

    
    // override this.NLoopOptions with the value we get from c-lightning.
    for op in options do
      let name = op.Key
      optsProperties
      |> Map.tryFind((PluginOptions.removePrefix name).ToLowerInvariant())
      |> Option.iter(fun p ->
        if p.PropertyType.IsArray then
          let values = (op.Value :?> IEnumerable<_>) |> Seq.map(fun v -> Convert.ChangeType(v, p.PropertyType.GetElementType()))
          
          this.SetArrayedProperty<int>(p, values,
            fun () -> this.SetArrayedProperty<int64>(p, values,
              fun () -> this.SetArrayedProperty<string>(p, values,
                fun () -> this.SetArrayedProperty<int16>(p, values)
              )
            )
          )
        else
          p.SetValue(this.NLoopOptions, Convert.ChangeType(op.Value, p.PropertyType))
      )

    // -- handle redundant option values those which defined in both c-lightning and nloop.
    let pairs =
      seq [
        ("network", "network")
        ("bitcoin-rpcuser", "btc.rpcuser")
        ("bitcoin-rpcpassword", "btc.rpcpassword")
        ("bitcoin-rpcconnect", "btc.rpchost")
        ("bitcoin-rpcport", "btc.rpcport")
      ]
    for lnName, nloopName in pairs do
      let valueForCln =
        match options.TryGetValue(PluginOptions.addPrefix lnName) with
        | true, p -> Some p
        | false, _ -> None
      let valueForNloop =
        match options.TryGetValue(PluginOptions.addPrefix nloopName) with
        | true, v -> Some v
        | false, _ -> None

      // override the value for nloop option iff user has passed the cln option.
      valueForCln
        |>
        Option.iter(fun v ->
          if valueForNloop.IsNone then
            let prop = optsProperties |> Map.find(nloopName.ToLowerInvariant())
            prop.SetValue(this.NLoopOptions, v)
        )
        
      // abort if there are conflicting options
      match valueForCln, valueForNloop with
      | Some cln, Some nloop when cln <> nloop ->
        let msg =
          $"mismatch in option. value for --{lnName} ({cln}) and {nloopName} ({nloop}) must have a same value. " +
          $" Or set {lnName} one only."
        logger.LogWarning(msg)
        failwith msg
      | _ -> ()
      
    optionsHolder.NLoopOptions <- Some this.NLoopOptions
    ()
    
  [<PluginJsonRpcSubscription("shutdown")>]
  member this.Shutdown() =
    logger.LogError "shutdown"
    applicationLifetime.StopApplication()

  [<PluginJsonRpcMethod("nloop_loopout", "initiate loopout swap", "initiate loop out swap")>]
  member this.LoopOut(req: NLoopClient.LoopOutRequest): Task<NLoopClient.LoopOutResponse> =
    backgroundTask {
      use! _releaser = this.AsyncSemaphore.EnterAsync()
      let req: LoopOutRequest = convertDTOToNLoopCompatibleStyle req
      let height = blockListener.CurrentHeight(req.PairIdValue.Base)
      match! swapExecutor.ExecNewLoopOut(req, height) with
      | Ok resp ->
        return resp |> convertDTOToJsonRPCStyle
      | Error e ->
        return raise <| exn e
    }

  [<PluginJsonRpcMethod("nloop_loopin", "initiate loop in swap", "initiate loop in swap")>]
  member this.LoopIn(req: NLoopClient.LoopInRequest) : Task<NLoopClient.LoopInResponse> =
    task {
      use! _releaser = this.AsyncSemaphore.EnterAsync()
      let req: LoopInRequest = convertDTOToNLoopCompatibleStyle req
      let height =
        blockListener.CurrentHeight req.PairIdValue.Quote
      match! swapExecutor.ExecNewLoopIn(req, height) with
      | Ok resp ->
        return resp |> convertDTOToJsonRPCStyle
      | Error e ->
        return raise <| exn e
    }

  [<PluginJsonRpcMethod(
    "nloop_swaphistory",
    "Get the full history of swaps.",
    "Get the full history of swaps. This might take long if you have a lots of entries in a database."
  )>]
  member this.SwapHistory() =
    task {
      use! _releaser = this.AsyncSemaphore.EnterAsync()
      return ()
    }

  [<PluginJsonRpcMethod(
    "nloop_ongoingswaps",
    "Get the list of ongoing swaps.",
    "Get the list of ongoing swaps."
  )>]
  member this.OngoingSwaps() =
    task {
      use! _releaser = this.AsyncSemaphore.EnterAsync()
      return ()
    }

  [<PluginJsonRpcMethod(
    "nloop_swapcostsummary",
    "Get the summary of the cost we paid for swaps.",
    "Get the summary of the cost we paid for swaps."
  )>]
  member this.SwapCostSummary() =
    task {
      use! _releaser = this.AsyncSemaphore.EnterAsync()
      return ()
    }

  [<PluginJsonRpcMethod(
    "nloop_suggestswaps",
    "Get suggestion for the swap based on autoloop settings.",
    "Get suggestion for the swaps. You must set liquidity parameters for autoloop before "
      + "getting the suggestion, this endpoint is usually useful when you set `autoloop=false` "
      + "to liquidity params. (that is, autoloop dry-run mode.). see `set_liquidityparams`"
  )>]
  member this.SuggestSwaps() =
    task {
      use! _releaser = this.AsyncSemaphore.EnterAsync()
      return ()
    }

  [<PluginJsonRpcMethod(
    "nloop_get_liquidityparams",
    "",
    ""
    )>]
  member this.Get_LiquidityParams(offChainAsset: NLoopClient.CryptoCode): Task<NLoopClient.LiquidityParameters> =
    task {
      use! _releaser = this.AsyncSemaphore.EnterAsync()
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

  [<PluginJsonRpcMethod(
    "nloop_set_liquidityparams",
    "",
    ""
  )>]
  member this.Set_LiquidityParams
    (
      liquidityParameters: NLoopClient.SetLiquidityParametersRequest,
      [<O;DefaultParameterValue(NLoopClient.CryptoCode.BTC)>]offchainAsset: NLoopClient.CryptoCode
    ): Task =
    task {
      use! _releaser = this.AsyncSemaphore.EnterAsync()
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

#endnowarn "3511"
