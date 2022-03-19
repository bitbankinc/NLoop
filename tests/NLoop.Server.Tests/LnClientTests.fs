namespace NLoop.Server.Tests

open System
open System.IO.Pipes
open System.Linq
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open LnClientDotnet
open Microsoft.Extensions.DependencyInjection
open NLoop.Server.DTOs
open NLoopClient
open Nerdbank.Streams
open Nerdbank.Streams
open Nerdbank.Streams
open StreamJsonRpc
open NLoop.Server
open Xunit

[<AutoOpen>]
module private ClightningClientTestHelpers =
  let utf8 = UTF8Encoding.UTF8

  let inline flatten (s: string) =
    s |> JsonSerializer.Deserialize<JsonDocument> |> JsonSerializer.Serialize

  let inline flattenObs a =
    a |> JsonSerializer.Serialize |> flatten

  [<Literal>]
  let private pipeName = "SamplePipeName"
  let private serverPipe () =
    new NamedPipeServerStream(
      pipeName,
      PipeDirection.InOut,
      NamedPipeServerStream.MaxAllowedServerInstances,
      PipeTransmissionMode.Byte,
      PipeOptions.Asynchronous
    )

  let private clientPipe() =
    new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous)
  let inline getClientProxy() =
    let formatter =
      let f = new JsonMessageFormatter()
      f

    let pipe = clientPipe()
    let handler = new NewLineDelimitedMessageHandler(pipe, pipe, formatter)
    task {
      do! pipe.ConnectAsync()
      return JsonRpc.Attach<INLoopJsonRpcServer> handler
    }

  let inline createRpcServer(server: INLoopJsonRpcServer) =
    let formatter = new JsonMessageFormatter()

    backgroundTask {
      while true do
        let pipe = serverPipe()
        do! pipe.WaitForConnectionAsync()
        let handler = new NewLineDelimitedMessageHandler(pipe, pipe, formatter)
        use rpc = new JsonRpc(handler)
        rpc.AddLocalRpcTarget<INLoopJsonRpcServer>(server, JsonRpcTargetOptions())
        rpc.StartListening()
        do! rpc.Completion
        ()
    }

type PluginTests() =
  [<Fact>]
  member  this.CanHandleMethod() =
      Environment.SetEnvironmentVariable("LIGHTNINGD_PLUGIN", "1")
      let dummyInit = $"""
  {{
    "id": 0,
    "method": "init",
    "params": {{
      "options": {{
        "greeting": "World",
        "number": [0]
      }},
      "configuration": {{
        "lightning-dir": "/home/user/.lightning/testnet",
        "rpc-file": "lightning-rpc",
        "startup": true,
        "network": "testnet",
        "feature_set": {{
            "init": "02aaa2",
            "node": "8000000002aaa2",
            "channel": "",
            "invoice": "028200"
        }},
        "proxy": {{
            "type": "ipv4",
            "address": "127.0.0.1",
            "port": 9050
        }},
        "torv3-enabled": true,
        "always_use_proxy": false
      }}
    }}
  }}
  """
      let initB =
        dummyInit
        |> flatten |> utf8.GetBytes
      let dummyMethodCallB =
        "{\"id\": 0,\"method\":\"testmethod\"}" |> flatten |> utf8.GetBytes
      use cts = new CancellationTokenSource()
      cts.CancelAfter(1000)
      let data = Array.concat [| initB; "\n\n" |> utf8.GetBytes; dummyMethodCallB  |]
      use inMem = new MemoryStream(data)
      use inStream = new StreamReader(inMem)
      use outMem = new MemoryStream(Array.zeroCreate 1024,0,1024,writable =true,publiclyVisible=true)
      let outStream = new StreamWriter(outMem)

      let childInitExecuted = TaskCompletionSource()
      let testMethodExecuted = TaskCompletionSource()
      let methodResult =
        {| test1 = "ok" |} |> box
      let t =
        Task.Run(fun () ->
          let methodHandler methodArg =
            testMethodExecuted.SetResult()
            methodResult
          Plugin.empty
          |> Plugin.setMockStdIn(inStream)
          |> Plugin.setMockStdOut(outStream)
          |> Plugin.addChildInit(fun arg ->
            childInitExecuted.SetResult()
          )
          |> Plugin.addMethodWithNoDescription
            "testmethod" methodHandler
          |> Plugin.runWithCancellation cts.Token
        , cts.Token)
      (task {
        do! childInitExecuted.Task
        do! testMethodExecuted.Task
        do! Task.Delay 100
        let result = outMem.ToArray() |> utf8.GetString
        Assert.Contains(methodResult |> flattenObs, result)
        let! _ = t
        ()
      }).GetAwaiter().GetResult()

  [<Fact>]
  member this.ServerStreamTests () =
    task {
      use cts = new CancellationTokenSource()
      use sp =
        TestHelpers.GetTestServiceProvider(fun (serviceCollection: IServiceCollection) ->
          serviceCollection.AddSingleton<INLoopJsonRpcServer, NLoopJsonRpcServer>() |> ignore
        )
      let rpcServer = sp.GetService<INLoopJsonRpcServer>()
      (*
      j.AddLocalRpcTarget(rpcServer)
      j.StartListening()

      let clientHandler = new NewLineDelimitedMessageHandler(clientToServerStream, serverToClientStream, formatter=new JsonMessageFormatter())
      let client: JsonRpc =
        JsonRpc.Attach(clientToServerStream, serverToClientStream)
        new JsonRpc(clientHandler)
      j.AddLocalRpcTarget(rpcServer)
      *)

      let serverTask = createRpcServer(rpcServer)
      let! client = getClientProxy()

      let! resp =
        client.Version()
        // client.InvokeWithCancellationAsync<string>("version", cancellationToken=cts.Token)
      Assert.NotNull(resp)
      //let! t = Task.WhenAny(j.Completion, Task.Delay(-1, cts.Token))
      //do! t
      ()
    }

  [<Fact>]
  member this.TestJsonRpcServer() =
    ()
