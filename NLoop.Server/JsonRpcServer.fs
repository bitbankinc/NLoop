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
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open NLoop.Domain.Utils
open NLoop.Server.Actors
open NLoop.Server.DTOs
open NLoop.Server.Handlers
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
open StreamJsonRpc.Protocol

// "state machine is not statically compilable" error.
// The performance does not really matters on the top level of the json-rpc. So ignore.
#nowarn "3511"

[<AutoOpen>]
module private JsonRpcServerHelpers =
  let stjOpts =
    JsonSerializerOptions(IgnoreNullValues = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
  stjOpts.AddNLoopJsonConverters()
  let newtonsoftOpts =
    JsonSerializerSettings(
      NullValueHandling = NullValueHandling.Include,
      MissingMemberHandling = MissingMemberHandling.Error
    )
  let inline convertDTOToNLoopCompatibleStyle(input: 'TIn) =
    let json =
      JsonConvert.SerializeObject(input, newtonsoftOpts)
    JsonSerializer.Deserialize<'T>(json, stjOpts)

  let inline convertDTOToJsonRPCStyle (input: 'TIn) : 'T =
    let json =
      JsonSerializer.Serialize<'TIn>(input, stjOpts)
    JsonConvert.DeserializeObject<'T>(json, newtonsoftOpts)

  type HandlerError with
    member this.AsJsonRpcErrorCode =
      match this with
      | HandlerError.InvalidRequest _ ->
        JsonRpcErrorCode.InvalidParams
      | HandlerError.InternalError _ ->
        JsonRpcErrorCode.InternalError
      | HandlerError.ServiceUnAvailable _ ->
        JsonRpcErrorCode.InternalError

[<RequireQualifiedAccess>]
module private Result =
  let internal unwrap (r: Result<_, HandlerError>) =
    match r with
    | Ok ok -> ok
    | Error e ->
      raise <| LocalRpcException(message = e.Message, ErrorCode = int e.AsJsonRpcErrorCode)
    
module PluginOptions =

  [<Literal>]
  let opPrefix = "nloop-"
  let addPrefix n = $"{opPrefix}{n}"
  let removePrefix (n: string) =
    let n = if n.StartsWith "--" then n.Substring(2) else n
    n.Substring(opPrefix.Length)

  let unnecessaryCliOptions =
    seq [
      "network"
    ]
    
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
    eventAggregator: IEventAggregator,
    loggerFactory: ILoggerFactory,
    applicationLifetime: IHostApplicationLifetime,
    optionsHolder: NLoopOptionsHolder,
    sp: IServiceProvider
  ) as _this =
  inherit PluginServerBase(Swap.AllTagEvents, true, loggerFactory.CreateLogger<PluginServerBase>().LogDebug)
  let logger: ILogger<NLoopJsonRpcServer> = loggerFactory.CreateLogger<_>()
  

  let _subscription =
    eventAggregator.GetObservable<RecordedEvent<Swap.Event>>()
    |> Observable.flatmapTask(fun _re ->
      backgroundTask {
        try
          // do! this.SendNotification(re.Data.Type, [|re|])
          ()
        with
        | ex ->
          logger.LogError(ex, "Failed to send CLightning notification")
      })
    |> Observable.subscribe id
  do
    logger.LogDebug $"NLoopJsonRpcServer Initialized"

  override this.Options =
    NLoopServerCommandLine.getOptions()
    |> Seq.filter(fun o ->
      PluginOptions.unnecessaryCliOptions
      |> Seq.contains o.Name
      |> not
    )
    |> Seq.map(PluginOptions.fromRootCLIOption)

  member private this.SetArrayedProperty<'T>(opts: NLoopOptions, p: Reflection.PropertyInfo, values, ?next) =
    try
      p.SetValue(opts, values |> Seq.cast<'T> |> Seq.toArray)
    with
    | :? InvalidCastException when next.IsSome ->
      next.Value()
      
  override this.InitCore(configuration, options) =
    
    let opts = NLoopOptions()

    let optsProperties =
      opts.GetType().GetProperties()
      |> Seq.map(fun p -> p.Name.ToLowerInvariant(), p)
      |> Map.ofSeq
    
    // -- override NLoopOptions fields with the value we get from c-lightning.
    for op in options do
      let name = op.Key
      optsProperties
      |> Map.tryFind((PluginOptions.removePrefix name).ToLowerInvariant())
      |> Option.iter(fun p ->
        if p.PropertyType.IsArray then
          let values = (op.Value :?> IEnumerable<_>) |> Seq.map(fun v -> Convert.ChangeType(v, p.PropertyType.GetElementType()))
          
          this.SetArrayedProperty<int>(opts, p, values,
            fun () -> this.SetArrayedProperty<int64>(opts, p, values,
              fun () -> this.SetArrayedProperty<string>(opts, p, values,
                fun () -> this.SetArrayedProperty<int16>(opts, p, values,
                   fun () -> this.SetArrayedProperty<SupportedCryptoCode>(opts, p, values,
                     fun () -> p.SetValue(opts, values |> Seq.cast<string>|> Seq.map(SupportedCryptoCode.Parse) |> Seq.toArray))
                 )
              )
            )
          )
        else
          p.SetValue(opts, Convert.ChangeType(op.Value, p.PropertyType))
      )
    
    // -- bind options from `configuration` field
    opts.Network <- configuration.Network
    opts.ClnRpcFile <- Path.Join(configuration.LightningDir, configuration.RpcFile)
      
    // -- bind ChainOptions
    let getOptionFromKeyName =
      fun (_cc: SupportedCryptoCode) (name: string) ->
        let key = PluginOptions.addPrefix($"{_cc.ToString().ToLowerInvariant()}.{name}").ToLowerInvariant()
        options.TryGetValue key |> snd
    opts.BindChainOptions(
      getOptionFromKeyName,
      (fun _ _ -> ())
    )
    
    optionsHolder.NLoopOptions <- Some opts
    
  [<PluginJsonRpcSubscription("shutdown")>]
  member this.Shutdown() =
    logger.LogInformation "shutting down ..."
    applicationLifetime.StopApplication()

  [<PluginJsonRpcMethod("nloop_loopout", "initiate loopout swap", "initiate loop out swap")>]
  member this.LoopOut(req: NLoopClient.LoopOutRequest): Task<NLoopClient.LoopOutResponse> =
    backgroundTask {
      let req: LoopOutRequest = convertDTOToNLoopCompatibleStyle req
      let! r =
        LoopHandlers.handleLoopOut
          (sp.GetRequiredService<GetOptions>())
          (sp.GetRequiredService<_>())
          (sp.GetRequiredService<_>())
          (sp.GetRequiredService<_>())
          (sp.GetRequiredService<_>())
          (loggerFactory.CreateLogger())
          req
      return r |> Result.unwrap |> convertDTOToJsonRPCStyle
    }

  [<PluginJsonRpcMethod("nloop_loopin", "initiate loop in swap", "initiate loop in swap")>]
  member this.LoopIn(req: NLoopClient.LoopInRequest) : Task<NLoopClient.LoopInResponse> =
    task {
      let req: LoopInRequest = convertDTOToNLoopCompatibleStyle req
      let! r =
        LoopHandlers.handleLoopIn
          (sp.GetRequiredService<_>())
          (sp.GetRequiredService<_>())
          (sp.GetRequiredService<_>())
          req
      return r |> Result.unwrap |> convertDTOToJsonRPCStyle
    }
    
  [<PluginJsonRpcMethod(
    "nloop_getinfo",
    "get nloop specific info",
    "get nloop specific info"
    )>]
  member this.GetInfo(): Task<NLoopClient.GetInfoResponse> =
      QueryHandlers.handleGetInfo
      |> convertDTOToJsonRPCStyle
      |> Task.FromResult

  [<PluginJsonRpcMethod(
    "nloop_swaphistory",
    "Get the full history of swaps.",
    "Get the full history of swaps. This might take long if you have a lots of entries in a database."
  )>]
  member this.SwapHistory([<O;DefaultParameterValue(Nullable(): Nullable<DateTime>)>]since)
    : Task<NLoopClient.GetSwapHistoryResponse>  =
    task {
      let! r =
        QueryHandlers.handleGetSwapHistory
          (sp.GetRequiredService<_>())
          (Option.ofNullable since)
      return r |> Result.unwrap |> convertDTOToJsonRPCStyle
    }

  [<PluginJsonRpcMethod(
    "nloop_ongoingswaps",
    "Get the list of ongoing swaps.",
    "Get the list of ongoing swaps."
  )>]
  member this.OngoingSwaps(): Task<NLoopClient.GetOngoingSwapResponse>  =
    task {
      return
        QueryHandlers.handleGetOngoingSwap
          (sp.GetRequiredService<_>())
        |> convertDTOToJsonRPCStyle
    }

  [<PluginJsonRpcMethod(
    "nloop_swapcostsummary",
    "Get the summary of the cost we paid for swaps.",
    "Get the summary of the cost we paid for swaps."
  )>]
  member this.SwapCostSummary([<O;DefaultParameterValue(Nullable(): Nullable<DateTime>)>]since): Task<NLoopClient.GetCostSummaryResponse> =
    task {
      let! r =
        QueryHandlers.handleGetCostSummary
          (Option.ofNullable since)
          (sp.GetRequiredService<_>())
          (sp.GetRequiredService<_>())
      return r |> Result.unwrap |> convertDTOToJsonRPCStyle
    }

  [<PluginJsonRpcMethod(
    "nloop_get_liquidityparams",
    "Get the liquidity params you have set before by set_liquidityparams",
    ""
    )>]
  member this.Get_LiquidityParams(
    [<O;DefaultParameterValue(NLoopClient.CryptoCode.BTC)>] offChainAsset: NLoopClient.CryptoCode
  ): Task<NLoopClient.LiquidityParameters> =
    task {
      let! r =
        AutoLoopHandlers.handleGetLiquidityParams
          (sp.GetRequiredService<_>())
          (convertDTOToNLoopCompatibleStyle offChainAsset)
      return r |> Result.unwrap |> convertDTOToJsonRPCStyle
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
      let req: SetLiquidityParametersRequest  = convertDTOToNLoopCompatibleStyle liquidityParameters
      let! r =
        AutoLoopHandlers.handleSetLiquidityParams
          (sp.GetRequiredService<_>())
          (sp.GetRequiredService<_>())
          req
          (offchainAsset |> convertDTOToNLoopCompatibleStyle |> Some)
      do r |> Result.unwrap
      return
        if req.Parameters.AutoMaxInFlight > 2 then
          "autoloop is experimental, usually it is not a good idea to set auto_max_inflight larger than 2"
        else
          null
    } :> Task
    
  [<PluginJsonRpcMethod(
    "nloop_suggestswaps",
    "Get suggestion for the swap based on autoloop settings.",
    "Get suggestion for the swaps. You must set liquidity parameters for autoloop before "
      + "getting the suggestion, this endpoint is usually useful when you set `autoloop=false` "
      + "to liquidity params. (that is, autoloop dry-run mode.). see `set_liquidityparams`"
  )>]
  member this.SuggestSwaps([<O;DefaultParameterValue(NLoopClient.CryptoCode.BTC)>]cryptoCode: NLoopClient.CryptoCode):
    Task<NLoopClient.SuggestSwapsResponse> =
    task {
      let! r =
        AutoLoopHandlers.handleSuggestSwaps
          (sp.GetRequiredService<_>())
          (cryptoCode |> convertDTOToNLoopCompatibleStyle)
      return r |> Result.unwrap |> convertDTOToJsonRPCStyle
    }

#endnowarn "3511"
