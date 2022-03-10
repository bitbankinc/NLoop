namespace rec LnClientDotnet

open System
open System.Reflection
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open NLoop.Domain.IO

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

type MethodResult = JsonElement

type MethodArg = {
  RPCClient: CLightningClient
  NotificationTopics: string list
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
type Subscription = delegate of MethodArg * Plugin -> unit

module private MethodFunc =
  let fromSimpleFunc (f: MethodArg -> MethodResult) =
    MethodFunc(fun arg plugin -> Some (f arg), plugin)

  let fromSubscription (f: Subscription) =
    MethodFunc(fun arg plugin -> f.Invoke(arg, plugin); None, plugin)

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
      After = [] }


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
  | Flag of bool
  with
  member this.StringValue =
    match this with
    | String s -> s
    | Int i -> int.ToString()
    | Bool v
    | Flag v -> v.ToString()

type PluginOptions = {
  Name: string
  Default: PluginOptValue option
  Description: string option
  OptType: PluginOptType
  Deprecated: bool
  Multi: bool
  Value: PluginOptType option
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
  Description: string
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
  | Str of stringId: string
  | Num of numId: int

type RPCError = {
  Error: CLightningRPCError
  Data: JsonElement option
}

[<RequireQualifiedAccess>]
type MethodParams =
  | ByPosition of JsonElement seq
  | ByName of Map<string, JsonElement>
  with
  member this.UnsafeGetObject() =
    match this with
    | ByName o -> o
    | ByPosition s -> raise <| InvalidDataException $"We thought arguments are object, got array: {s}"
  member this.UnsafeGetArray() =
    match this with
    | ByPosition s  -> s
    | ByName o -> raise <| InvalidDataException $"We thought arguments are array, got array: {o}"

type Request = {
  Method: string
  Params: MethodParams
  Background: bool
  Id: JsonRPCV2Id option
}

type Plugin = private {
  Options: Map<string, PluginOptions>
  Methods: Map<string, Method>
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
    let parts =
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
      sb.Append(optionsTemplate opt.Name ty de desc) |> ignore
    )

    Console.WriteLine (sb.ToString())
    Console.WriteLine "\n\n"

  member this.CallbackArg = this.RPC

  member internal this.WriteLocked(value: string) =

    let jsonValue =
      let opts = JsonSerializerOptions()
      opts.AddNLoopJsonConverters()
      JsonSerializer.Serialize(value, opts)
    lock this._writeLock <| fun () ->
      this.StdOut.Write(jsonValue)
      this.StdOut.Flush()

  member internal this.ParseRequest (jsonRequest: JsonElement): Request =
    match jsonRequest.TryGetProperty "id" with
    | true, i when i.ValueKind <> JsonValueKind.Number ->
      raise <| InvalidDataException $"Non-integer request id {i}"
    | hasId, i ->
      {
        Method = jsonRequest.GetProperty("method").ToString()
        Params =
          match jsonRequest.GetProperty("params") with
          | j when j.ValueKind = JsonValueKind.Array ->
            MethodParams.ByPosition (j.EnumerateArray() :> seq<JsonElement>)
          | j when j.ValueKind = JsonValueKind.Object ->
            MethodParams.ByName (j.EnumerateObject() |> Seq.map(fun o -> (o.Name, o.Value)) |> Map.ofSeq )
        Background = false
        Id = if hasId then i.GetInt32() |> JsonRPCV2Id.Num |> Some else None
      }

[<RequireQualifiedAccess>]
module Plugin =

  let private execFunc (func: MethodFunc) (req: Request) (p: Plugin): MethodResult option * Plugin =
    let methodArg =
      {
        MethodArg.Request = req
        FeatureBits = p.FeatureBits
        RPCClient = p.RPC.Value
        NotificationTopics = p.NotificationTopics
        LightningVersion = p.LightningVersion
      }
    func.Invoke(methodArg, p)

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
            Description = failwith "todo"
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
        let p = o.Request.Params.UnsafeGetObject()
        let configurations = p |> Map.find("configuration")
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
        let opts = JsonSerializerOptions()
        opts.AddNLoopJsonConverters()
        let nextState = {
          plugin
            with
            RpcFileName = Some rpcFileName
            LightningDir = Some lightningDir
            RPC =
              let path = Path.Combine(lightningDir, rpcFileName)
              Some <| CLightningClient(path)
            Startup = verifyBool(configurations, "startup")
            Options =
              let options = p |> Map.find("options")
              options.EnumerateArray()
              |> Seq.fold(fun (acc: Map<string, PluginOptions>) (optionObj: JsonElement) ->
                  acc
                  |> Map.add
                       (optionObj.GetProperty("name").GetString())
                       (JsonSerializer.Deserialize<PluginOptions>(optionObj, opts))
                )
                Map.empty
        }
        match nextState.ChildInit with
        | Some i -> i.Invoke(o, nextState)
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
        let res, nextState = execFunc method.Func req p
        match res with
        | Some r when not <| method.Background ->
          let resultString = JsonSerializer.Serialize(r)
          let v = $"{{'jsonrpc':'2.0','id':{req.Id},'result':'{r}'}}"
          p.WriteLocked(v)
        nextState
      with
      | ex ->
        failwith "todo: handle error"
        p

  let private dispatchNotification(req: Request) (p: Plugin): Plugin =
    let name = req.Method
    match p.Subscriptions |> Map.tryFind name with
    | None ->
        raise <| InvalidDataException $"No subscription for {name} found."
    | Some subsc ->
      try
        let _ = execFunc (MethodFunc.fromSubscription subsc) req
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

  /// We received a couple of messages, now try to dispatch them all.
  /// Returns the last partial message that was not complete yet.
  let private multiDispatch(msgs: string[]) (p: Plugin): Plugin =
    msgs |> Array.fold(fun acc msg -> dispatch msg acc) p

  let empty = {
    Plugin.Options = Map.empty
    Methods =
      Map.empty
      |> Map.add "init" (Method.Create(init, MethodType.RPCMETHOD, MethodMetadata.CreateFromName "init"))
      |> Map.add "getmanifest" (Method.Create(getManifest, MethodType.RPCMETHOD, MethodMetadata.CreateFromName "getmanifest"))
    NotificationTopics = []
    FeatureBits = FeatureBitSet.Zero
    Subscriptions = Map.empty
    LightningVersion = Environment.GetEnvironmentVariable("LIGHTNINGD_VERSION") |> Option.ofObj
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

  let addMethod (meta: MethodMetadata) (method: MethodArg -> MethodResult) (p: Plugin) =
    if p.Methods |> Map.containsKey meta.Name then
      raise <| ArgumentException $"method: {meta.Name} Already registered"
    {
      p
        with
        Methods = p.Methods |> Map.add meta.Name (Method.Create(MethodFunc.fromSimpleFunc method, MethodType.RPCMETHOD, meta))
    }

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

  let addBackgroundHook name func before after (p: Plugin) =
    addHook name func true before after p
  let addBackgroundSimpleHook name func (p: Plugin) =
    addHook name func true [] [] p

  let run (p: Plugin): unit =
    let v = Environment.GetEnvironmentVariable "LIGHTNINGD_PLUGIN"
    if v <> "1" then
      p.PrintUsage()
    else
      let partial = ResizeArray<string>()
      let mutable state = p
      while true do
        let s = p.StdIn.ReadLine()
        partial.Add(s)
        if partial.Count < 2 then () else
        state <- multiDispatch (partial.ToArray()) p
        partial.Clear()

