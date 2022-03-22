namespace rec LnClientDotnet

open System
open System.Reflection
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open Newtonsoft.Json.Serialization
open Newtonsoft.Json.Serialization

type FeatureBits = DotNetLightning.Serialization.FeatureBits

type FeatureBitSet = {
  InitFeatureBits: FeatureBits
  NodeFeatureBits: FeatureBits
  InvoiceFeatureBits: FeatureBits
}
  with
  static member Zero = {
    InitFeatureBits = FeatureBits.Zero
    NodeFeatureBits = FeatureBits.Zero
    InvoiceFeatureBits = FeatureBits.Zero
  }

type MethodType =
  | RPCMETHOD = 0
  | HOOK = 1

type RequestState =
  | Pending
  | Finished
  | Failed
  with
  member this.Value =
    match this with
    | Pending -> "pending"
    | Finished -> "finished"
    | Failed -> "failed"


[<NoEquality;NoComparison>]
type Hook = {
  Name: string
  Before: string list
  After: string list
}

/// json to return lightningd
type MethodResult = obj

type MethodArg = {
  RPCClient: CLightningClient
  FeatureBits: FeatureBitSet
  LightningVersion: string option
  Request: Request
}
type MethodMetadata = {
  Name: string
  Category: string option
  Desc: string option
  LongDesc: string option
  Deprecated: bool
}
  with
  static member CreateFromName(n: string) =
    {
      Name = n
      Category = None
      Desc = None
      LongDesc = None
      Deprecated = false
    }

type MethodFunc = delegate of MethodArg * Plugin -> MethodResult option * Plugin

type SubscriptionArg = {
  RPCClient: CLightningClient option
  NotificationTopics: string list
  FeatureBits: FeatureBitSet
  LightningVersion: string option
  Request: Request
}
type Subscription = delegate of SubscriptionArg * Plugin -> unit

module private MethodFunc =
  let fromSimpleFunc (f: MethodArg -> MethodResult) =
    MethodFunc(fun arg plugin -> Some (f arg), plugin)

  let fromSimpleAction(f: MethodArg -> unit) =
    MethodFunc(fun arg plugin -> f arg; None, plugin)

module private Subscription =
  let fromSimpleFunc (f: SubscriptionArg -> unit) =
    Subscription(fun arg _plugin -> f arg;)

type [<NoEquality;NoComparison>] Method = internal {
  Func: MethodFunc
  MethodType: MethodType
  Background: bool
  Metadata: MethodMetadata
  Before: string list
  After: string list
}
  with
  member this.AsHook = {
    Hook.Name = this.Metadata.Name
    Before = this.Before
    After = this.After
  }
  static member Create(func: MethodFunc,
                       [<O;D(MethodType.RPCMETHOD)>] methodType,
                       methodMetadata: MethodMetadata) =
    {
      Func = func
      MethodType = methodType
      Background = false
      Metadata = methodMetadata
      Before = []
      After = []
    }


type PluginOptType =
  | String = 0
  | Int = 1
  | Bool = 2
  | Flag = 3

[<RequireQualifiedAccess>]
type PluginOptValue =
  | String of string
  | Int of int
  | Bool of bool
  with
  member this.StringValue =
    match this with
    | String s -> s
    | Int i -> int.ToString()
    | Bool v -> v.ToString()

  static member FromJsonElement(j: JsonElement) =
    match j.ValueKind with
    | JsonValueKind.String -> String (j.Deserialize())
    | JsonValueKind.Number -> Int (j.Deserialize())
    | JsonValueKind.True | JsonValueKind.False -> Bool (j.Deserialize())
    | x -> raise <| Exception $"Invalid json type from lightningd {x}"

type PluginOptions = {
  Name: string option
  Default: PluginOptValue option
  Description: string option
  OptType: PluginOptType
  Deprecated: bool
  Multi: bool
  Value: PluginOptValue option
}
  with
  static member Create(name,
                       [<O;D(null)>] defaultValue,
                       [<O;D(null)>] description,
                       [<O;D(PluginOptType.String)>] optType,
                       [<O;D(false)>] deprecated,
                       [<O;D(false)>] multi
                       ) =
    {
      Name = name
      Default = defaultValue |> Some
      Description = description |> Option.ofObj
      OptType = optType
      Deprecated = deprecated
      Multi = multi
      Value = None
    }

[<NoComparison;NoEquality>]
type RPCMethod = {
  Name: string
  Category: string
  Usage: string
  Description: string option
  LongDescription: string option
}
type Manifest = {
  Options: PluginOptions ICollection
  RPCMethods: RPCMethod list
  Subscriptions: string ICollection
  Hooks: Hook list
  Dynamic: bool
  Notifications: {| Method: string |} list
  FeatureBits: FeatureBitSet option
}

[<Struct>]
type JsonRPCV2Id =
  | Num of numId: int
  with
  member this.Value =
    match this with
    | Num i -> i

type RPCError = {
  Error: CLightningRPCError
  Data: JsonElement option
}

[<RequireQualifiedAccess>]
type MethodParams =
  | ByPosition of JsonElement seq
  | ByName of Map<string, JsonElement>
  | None
  with
  member this.UnsafeGetObject() =
    match this with
    | ByName o -> o
    | ByPosition s ->
      raise <| InvalidDataException $"We thought arguments are object, got array: {s}"
    | None ->
      raise <| InvalidDataException $"We thought arguments are object, got None"

  member this.UnsafeGetArray() =
    match this with
    | ByPosition s  -> s
    | ByName o ->
      raise <| InvalidDataException $"We thought arguments are array, got array: {o}"
    | None ->
      raise <| InvalidDataException $"We thought arguments are array, got None"

type Request = {
  Method: string
  Params: MethodParams
  Background: bool
  Id: JsonRPCV2Id option
}

type Response = {
  [<JsonPropertyName "jsonrpc">]
  JsonRpcVersion: string
  [<JsonPropertyName "id">]
  Id: int
  [<JsonPropertyName "result">]
  Result: MethodResult
}

type Plugin = private {
  /// Map of plugin name -> options it takes on startup.
  Options: Map<string, PluginOptions>
  /// Map of method name -> registered handler
  Methods: Map<string, Method>
  /// The notification topics which the plugin may return to the lightningd.
  NotificationTopics: string list
  FeatureBits: FeatureBitSet
  Subscriptions: Map<string, Subscription>
  LightningVersion: string option
  ChildInit: MethodFunc option
  DeprecatedAPIs: bool
  Dynamic: bool
  Startup: bool
  LightningDir: string option
  RpcFileName: string option
  RPC: CLightningClient option
  StdIn: TextReader
  StdOut: TextWriter
  _writeLock: obj
}
  with
  member internal this.PrintUsage() =
    let overview =
      """
      Hi, it looks like you're trying to run a plugin from the
      command line. Plugins are usually started and controlled by
      lightningd, which allows you to simply specify which plugins
      you'd like to run using the --plugin command line option when
      starting lightningd. The following is an example of how that'd
      look:

        $ lightningd --plugin={executable}

      If lightningd is already running, you can also start a plugin
      by using the cli:

        $ lightning-cli plugin start /path/to/a/plugin

      Since we're here however let me tell you about this plugin.
      """

    let sb = StringBuilder()
    sb.Append(overview) |> ignore
    let mutable methodHeader =
      """
      RPC methods
      ===========

      Plugins may provide additional RPC methods that you can simply
      call as if they were built-in methods from lightningd
      itself. To call them just use lightning-cli or any other
      frontend. The following methods are defined by this plugin:
      """
    let methodTemplate =
      sprintf
        """
          %s
        %s
        """
    this.Methods
    |> Map.filter(fun k v -> k <> "init" && k <> "getmanifest" && v.MethodType = MethodType.RPCMETHOD)
    |> Map.iter(fun name method ->
      if methodHeader |> isNull |> not then
        sb.Append(methodHeader) |> ignore
        methodHeader <- null
      let doc = method.Metadata.LongDesc |> Option.defaultValue "No documentation found"
      sb.Append(methodTemplate name doc) |> ignore
      ()
    )

    let optionsHeader =
      """
      Command line options
      ====================
      This plugin exposes the following command line options. They
      can be specified just like any other you might gice lightning
      at startup. The following options are exposed by this plugin:
      """

    let optionsTemplate =
      sprintf
        """
        --%s=%s (default: %s)
        %s
        """
    if this.Options.IsEmpty |> not then
        sb.Append(optionsHeader) |> ignore
    this.Options
    |> Map.iter(fun k opt ->
      let desc = opt.Description |> Option.defaultValue ""
      if opt.Multi then
        sb.Append("\n This option can be specified multiple times") |> ignore
      let ty = (opt.OptType.ToString().ToLowerInvariant())
      let de = opt.Default |> Option.map(fun d -> d.StringValue) |> Option.defaultValue "none"
      sb.Append(optionsTemplate (opt.Name |> Option.defaultValue "None") ty de desc) |> ignore
    )

    Console.WriteLine (sb.ToString())
    Console.WriteLine "\n\n"

  member this.CallbackArg = this.RPC

  member internal this.WriteLocked(value: string) =
    lock this._writeLock <| fun () ->
      this.StdOut.Write(value)
      this.StdOut.Flush()

  member internal this.ParseRequest (jsonRequest: JsonElement): Request =
    match jsonRequest.TryGetProperty "id" with
    | true, i when i.ValueKind <> JsonValueKind.Number ->
      raise <| InvalidDataException $"Non-integer request id {i}"
    | hasId, i ->
      {
        Method =
          match jsonRequest.TryGetProperty("method") with
          | true, m -> m.ToString()
          | _ ->
            let msg = "Invalid json! No \"method\" property"
            Console.Error.WriteLine(msg)
            raise <| Exception msg
        Params =
          match jsonRequest.TryGetProperty("params") with
          | true, j when j.ValueKind = JsonValueKind.Array ->
            MethodParams.ByPosition (j.EnumerateArray() :> seq<JsonElement>)
          | true, j when j.ValueKind = JsonValueKind.Object ->
            MethodParams.ByName (j.EnumerateObject() |> Seq.map(fun o -> (o.Name, o.Value)) |> Map.ofSeq )
          | false, _ ->
            MethodParams.None
          | true, j ->
            let msg = $"Invalid json! \"params\" property was {j.ToString()}"
            Console.Error.WriteLine(msg)
            raise <| Exception msg
        Background = false
        Id = if hasId then i.GetInt32() |> JsonRPCV2Id.Num |> Some else None
      }

[<RequireQualifiedAccess>]
module Plugin =

  let private execMethod isInit (func: MethodFunc) (req: Request) (p: Plugin): MethodResult option * Plugin =
    let methodArg =
      {
        MethodArg.Request = req
        MethodArg.FeatureBits = p.FeatureBits
        MethodArg.RPCClient = if isInit then Unchecked.defaultof<_> else p.RPC.Value
        MethodArg.LightningVersion = p.LightningVersion
      }
    func.Invoke(methodArg, p)

  let private execSubscription (subsc: Subscription) (req: Request) (p: Plugin) : unit =
    let arg = {
      SubscriptionArg.Request = req
      RPCClient = p.RPC
      NotificationTopics = p.NotificationTopics
      FeatureBits = p.FeatureBits
      LightningVersion = p.LightningVersion
    }
    subsc.Invoke(arg, p)

  let getManifest: MethodFunc =
    let f (methodarg: MethodArg) (p: Plugin) =
      let p =
        let arguments = methodarg.Request.Params.UnsafeGetObject()
        match arguments |> Map.tryFind "allow-deprecated-apis" with
        | Some deprecated when deprecated.ValueKind = JsonValueKind.True || deprecated.ValueKind = JsonValueKind.False  ->
          { p with DeprecatedAPIs = deprecated.GetBoolean() }
        | _ ->
          { p with DeprecatedAPIs = true }

      let hooks, methods =
        p.Methods
        |> Map.filter(fun k _ -> k <> "getmanifest" && k <> "init")
        |> Map.partition (fun _ v -> v.MethodType = MethodType.HOOK)
      let hooks =
        hooks
        |> Seq.toList
        |> List.map(fun kv -> kv.Value.AsHook)

      let methods =
        methods.Values
        |> Seq.toList
        |> Seq.map (fun m ->
          let argSpec =
            m.Func.GetMethodInfo().GetParameters()
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
            RPCMethod.Name = m.Metadata.Name
            Category = m.Metadata.Category |> Option.defaultValue "plugin"
            Usage = String.Join(' ', args)
            Description = m.Metadata.Desc
            LongDescription = m.Metadata.LongDesc
          }
        )
      let manifest = {
        Manifest.Options = p.Options.Values
        RPCMethods = methods |> Seq.toList
        Subscriptions = p.Subscriptions.Keys
        Hooks = hooks
        Dynamic = p.Dynamic
        Notifications =
          p.NotificationTopics |> List.map(fun n -> {| Method = n |})
        FeatureBits = p.FeatureBits |> Some
      }
      None, p
    MethodFunc f

  let private init: MethodFunc =
    let f
      (o: MethodArg) (plugin: Plugin) =
        let reqParams = o.Request.Params.UnsafeGetObject()
        let configurations = reqParams |> Map.find("configuration")
        let verifyStr(d: JsonElement, key: string) =
          match d.TryGetProperty key with
          | true, v when v.ValueKind = JsonValueKind.String ->
            v.Deserialize()
          | _, v ->
            raise <| InvalidDataException $"wrong argument to init: expected {key} to be string. ot {v}"

        let verifyBool (d: JsonElement, key: string) =
          match d.TryGetProperty key with
          | true, v when v.ValueKind = JsonValueKind.True || v.ValueKind = JsonValueKind.False -> v.Deserialize()
          | _, v ->
            raise <| InvalidDataException $"Wrong argument to init: expected {key} to be a bool. got {v}"

        let lightningDir = verifyStr(configurations, "lightning-dir")
        let rpcFileName = verifyStr(configurations, "rpc-file")
        let nextState = {
          plugin
            with
            RpcFileName = Some rpcFileName
            LightningDir = Some lightningDir
            RPC =
              let path = Path.Combine(lightningDir, rpcFileName)
              Some <| CLightningClient(Uri($"unix://{path}"))
            Startup = verifyBool(configurations, "startup")
            Options =
              (
              match reqParams |> Map.find("options") with
              | o when o.ValueKind = JsonValueKind.Object ->
                o.EnumerateObject()
                |> Seq.map(fun o -> (o.Name, o.Value))
              | o -> raise <| Exception $"Invalid init options from lightningd: {o}"
              )
              |> Seq.fold(fun (acc: Map<string, PluginOptions>) ((name, value): string * JsonElement) ->
                  acc
                  |> Map.change
                       name
                       (Option.map(fun o -> { o with Value = PluginOptValue.FromJsonElement value |> Some }))
                ) plugin.Options
        }
        match nextState.ChildInit with
        | Some i ->
          i.Invoke(o, nextState)
        | None ->
          None, nextState
    MethodFunc f

  let private dispatchRequest(req: Request) (p: Plugin) =
    let name = req.Method
    match p.Methods |> Map.tryFind name with
    | None ->
        raise <| InvalidDataException $"method {name} not found"
    | Some method ->
      let req = { req with Background = method.Background }
      try
        let res, nextState = execMethod (name = "init") method.Func req p
        match res with
        | Some r when r |> isNull |> not && r.ToString() |> String.IsNullOrWhiteSpace |> not && not <| method.Background ->
          let v = {
            Response.JsonRpcVersion = "2.0"
            Id = req.Id.Value.Value
            Result = r
          }
          p.WriteLocked(JsonSerializer.Serialize(v))
        | _ ->
          ()
        nextState
      with
      | ex ->
        Console.Error.WriteLine(ex.ToString())
        reraise()

  let private dispatchNotification(req: Request) (p: Plugin): Plugin =
    let name = req.Method
    match p.Subscriptions |> Map.tryFind name with
    | None ->
        raise <| InvalidDataException $"No subscription for {name} found."
    | Some subsc ->
      try
        let _ = execSubscription subsc req
        ()
      with
      | ex ->
        ()
      p

  let private dispatch (msg: string) (p: Plugin): Plugin =
    let req = p.ParseRequest(JsonSerializer.Deserialize(msg))
    match req.Id with
    | Some _ -> dispatchRequest req p
    | None -> dispatchNotification req p

  let empty = {
    Plugin.Options = Map.empty
    Methods =
      Map.empty
      |> Map.add "init" (Method.Create(init, MethodType.RPCMETHOD, MethodMetadata.CreateFromName "init"))
      |> Map.add "getmanifest" (Method.Create(getManifest, MethodType.RPCMETHOD, MethodMetadata.CreateFromName "getmanifest"))
    NotificationTopics = []
    FeatureBits = FeatureBitSet.Zero
    Subscriptions = Map.empty
    LightningVersion =
      Environment.GetEnvironmentVariable("LIGHTNINGD_VERSION") |> Option.ofObj
    ChildInit = None
    DeprecatedAPIs = true
    Dynamic = true
    Startup = true
    LightningDir = None
    RpcFileName = None
    RPC = None
    StdIn = Console.In
    StdOut = Console.Out
    _writeLock = obj()
  }

  let setMockStdIn (s: TextReader) (p: Plugin) =
    {
      p
      with
        StdIn = s
    }
  let setMockStdOut (s: TextWriter) (p: Plugin) =
    {
      p
      with
        StdOut = s
    }

  let asDynamic (p: Plugin) =
    {
      p
        with
        Dynamic = true
    }
  let asStatic (p: Plugin) =
    {
      p
        with
        Dynamic = false
    }

  let setLightningDir dir (p: Plugin) =
    {
      p
      with
        LightningDir = Some dir
    }

  let setRPCClient client (p: Plugin) =
    {
      p
      with
        RPC = Some client
    }

  let setRPCFilePath path (p: Plugin) =
    {
      p
      with
        RpcFileName = Some path
    }

  /// Add new RPC method to the lightningd.
  /// lightningd will bypass the rpc call to the handler.
  /// Handler must return the json object to the lightningd, which finally returned to the caller.
  let addMethod (meta: MethodMetadata) (method: MethodArg -> MethodResult) (p: Plugin) =
    if p.Methods |> Map.containsKey meta.Name then
      raise <| ArgumentException $"method: {meta.Name} Already registered"
    {
      p
        with
        Methods = p.Methods |> Map.add meta.Name (Method.Create(MethodFunc.fromSimpleFunc method, MethodType.RPCMETHOD, meta))
    }

  /// Simple version of the `addMethod`, it is recommended to use `addMethod`
  /// For any serious application.
  let addMethodWithNoDescription (name: string) (method: MethodArg -> MethodResult) (p: Plugin) =
    addMethod (MethodMetadata.CreateFromName name) method p

  let addSubscription (topic: string) func (p: Plugin) =
    if p.Subscriptions |> Map.containsKey topic then
      raise <| ArgumentException $"subscription: {topic} Already registered"
    {
      p
        with
        Subscriptions = p.Subscriptions |> Map.add topic func
    }

  /// add an startup option for the plugin.
  /// lightningd will take this option on startup and passes it to the plugin.
  let addOption name (options: PluginOptions) (p: Plugin) =
    if p.Options |> Map.containsKey name then
      raise <| ArgumentException $"option: {name} Already registered"
    {
      p
        with
        Options = p.Options |> Map.add name options
    }

  let addHook name func background (before: _ list) (after: _ list) (p: Plugin) =
    let method ={
      Method.Create(func, MethodType.HOOK, MethodMetadata.CreateFromName name)
        with
        Background = background
        Before = before
        After = after
    }
    {
      p
        with
        Methods = p.Methods |> Map.add name method
    }
  let addSimpleHook name func background (p: Plugin) =
    addHook name func background [] [] p

  /// Add a function called after plugin initialization
  let addChildInit (func: MethodArg -> unit) (p: Plugin) =
    {
      p
        with
        ChildInit = MethodFunc.fromSimpleAction func |> Some
    }
  let addBackgroundHook name func before after (p: Plugin) =
    addHook name func true before after p
  let addBackgroundSimpleHook name func (p: Plugin) =
    addHook name func true [] [] p

  let runWithCancellation (ct: CancellationToken) (p: Plugin) =
    let v = Environment.GetEnvironmentVariable "LIGHTNINGD_PLUGIN"
    if v <> "1" then
      p.PrintUsage()
    else
        let mutable state = p
        try
          while not <| ct.IsCancellationRequested do
            let s = p.StdIn.ReadLine()
            if s |> String.IsNullOrWhiteSpace then () else
            state <- dispatch s state
        with
        | ex ->
          Console.Error.Write(ex.ToString())
          reraise()
  let run (p: Plugin) = runWithCancellation CancellationToken.None p
