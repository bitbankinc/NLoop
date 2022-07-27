namespace NLoop.Server.Tests

open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open DotNetLightning.ClnRpc.Plugin
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open NLoop.Server
open Xunit

type JsonRpcTests() =
  let utf8 = UTF8Encoding.UTF8

  let flatten(s: string) =
      s |> JsonSerializer.Deserialize<JsonDocument> |> JsonSerializer.Serialize

  let initStr =
      $"""
    {{
      "id": 0,
      "method": "init",
      "jsonrpc": "2.0",
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

  let initB = initStr |> flatten |> utf8.GetBytes
  [<Fact(Skip="tmp")>]
  member this.InitCoreTests() =
    use server =
      new TestServer(TestHelpers.GetTestHost(fun builder ->
        builder.AddSingleton<NLoopJsonRpcServer>() |> ignore
      ))
    let rpcServer = server.Services.GetRequiredService<NLoopJsonRpcServer>()
    let config = {
      LightningInitConfigurationDTO.Network = "testnet"
      LightningDir = "/path/to/ln_dir"
      RpcFile = "lightning-rpc"
      Startup = true
      FeatureSet = {
        Init = None
        Node = None
        Channel = None
        Invoice = None
      }
      Proxy = {
        ProxyDTO.Address = "foobar"
        Ty = "ipv4"
        Port = 1000
      }
      TorV3Enabled = false
      AlwaysUseProxy = false
    }
    let opts = Dictionary<string, obj>()
    // test server is run in regtest. So it must throw an error.
    opts.Add("network", "testnet")
    let e = Assert.ThrowsAny(fun () -> rpcServer.InitCore(config, opts))
    Assert.Contains("mismatch in option", e.Message)
    ()

  [<Fact(Skip="tmp")>]
  member this.PluginModeTest() =
    task {
      use! host =
        TestHelpers.GetPluginTestHost()

      use cts = new CancellationTokenSource()
      cts.CancelAfter(2000)
      let server = host.Services.GetRequiredService<NLoopJsonRpcServer>()
      use outStream = new MemoryStream(Array.zeroCreate (65535 * 16))

      let buf = Array.concat [| initB |]
      use inStream = new MemoryStream(buf)
      let! _ =
        server.StartAsync(outStream, inStream, cts.Token)
      ()
      ()
    }
