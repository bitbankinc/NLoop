namespace NLoop.Server

open System
open System.Collections.Generic
open System.IO
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open NBitcoin.Logging
open NLoop.Server
open Newtonsoft.Json
open StreamJsonRpc
open StreamJsonRpc.Protocol

type PluginLoggerOptions() =
    member val JsonSettings: JsonSerializerSettings = null

type PluginLogger
    (
        name: string,
        filter: Func<string, LogLevel, bool>,
        scopeProvider: IExternalScopeProvider,
        opts: PluginLoggerOptions,
        outputStream: Stream
    ) =
    let filter = if filter |> isNull then Func<_,_,_>(fun (_: string) (_: LogLevel) -> true)  else filter
    do if name |> isNull then raise <| ArgumentNullException(nameof(name))
    
    static let getLogLevelString(logLevel: LogLevel) =
        match logLevel with
        | LogLevel.Trace -> "debug"
        | LogLevel.Debug -> "debug"
        | LogLevel.Information -> "info"
        | LogLevel.Warning -> "warn"
        | LogLevel.Error -> "error"
        | LogLevel.Critical -> "error"
        | _ ->
            raise <| ArgumentOutOfRangeException(nameof(logLevel))
            
    let isEnabled (logLevel: LogLevel) =
        logLevel <> LogLevel.None && filter.Invoke(name, logLevel)
    
    member private this.WriteMsg(logLevel: LogLevel, logName: string, msg: string, ex: exn) =
        let json = JsonSerializer.Create(opts.JsonSettings)
        
        let textWriter = new StreamWriter(outputStream)
        let jsonWriter = new JsonTextWriter(textWriter)
        for line in msg.Split(Environment.NewLine) do
            let msg = $"{logName}: {line}"
            let req = JsonRpcRequest()
            req.Method <- "log"
            req.NamedArguments <-
                let d = Dictionary<string, obj>()
                for item in
                    [
                        ("level", getLogLevelString logLevel)
                        ("message", msg)
                    ]
                    do
                    d.Add(item)
                d
                
            json.Serialize(jsonWriter, req)
            
        if ex |> isNull |> not then
            let code =
                match ex with
                | :? LocalRpcException as e ->
                    e.ErrorCode
                | _ -> JsonRpcErrorCode.InternalError |> int
                
            let msg = $"{logName}: {ex.Message}"
            Console.Error.WriteLine(msg)
            let req =
                let r = JsonRpcRequest()
                r.NamedArguments <-
                    let errorObj =
                        let d = Dictionary<string, obj>()
                        for item in [
                            ("code", code |> box)
                            ("message", msg)
                            ("traceback", (if ex |> isNull then null else ex.StackTrace |> box))
                        ] do
                            d.Add(item)
                        d
                    let d = Dictionary<string, obj>()
                    for item in
                        [
                            ("error", errorObj)
                        ] do
                        d.Add(item)
                    d
                r
            json.Serialize(jsonWriter, req)
            
        match logLevel with
        | LogLevel.Critical
        | LogLevel.Error ->
            Console.Error.WriteLine($"{logName}: {msg}")
            ()
        | _ -> ()
            
        jsonWriter.Flush()
        textWriter.Flush()
        outputStream.Flush()
        ()
    
    interface ILogger with
        member this.BeginScope(state) =
            if scopeProvider |> isNull |> not then scopeProvider.Push(state) else
                NullScope.Instance
        member this.IsEnabled(logLevel) = isEnabled(logLevel)
        member this.Log(logLevel, _eventId, state, ``exception``, formatter) =
            if not <| isEnabled(logLevel) then () else
            if formatter |> isNull then
                raise <| ArgumentNullException(nameof(formatter))
            let msg = formatter.Invoke(state, ``exception``)
            
            if (not <| String.IsNullOrEmpty(msg) || ``exception``<> null) then
                this.WriteMsg(logLevel, name, msg, ``exception``)
            else
                ()
    
type PluginLoggerProvider(configure: Action<PluginLoggerOptions>, outputStream: Stream) =
   let opts = PluginLoggerOptions()
   do
       if configure |> isNull then raise <| ArgumentNullException(nameof(configure))
       configure.Invoke opts
   interface ILoggerProvider with
       member this.CreateLogger(categoryName) =
           PluginLogger(categoryName, Func<string, LogLevel, bool>(fun _ _ -> true), null, opts, outputStream)
       member this.Dispose() = ()
       
open Microsoft.Extensions.DependencyInjection.Extensions
[<AutoOpen>]
module PluginLoggerExtensions =
    type ILoggingBuilder with
        member this.AddPluginLogger(configure: Action<PluginLoggerOptions>, outputStream: Stream) =
            this.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, PluginLoggerProvider>(fun _ -> new PluginLoggerProvider(configure, outputStream)))
            this
        member this.AddPluginLogger(outputStream: Stream) =
            this.AddPluginLogger(ignore, outputStream)
