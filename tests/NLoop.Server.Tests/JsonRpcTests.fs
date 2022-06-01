namespace NLoop.Server.Tests

open System.Collections.Generic
open DotNetLightning.ClnRpc.Plugin
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open NLoop.Server
open Xunit

type JsonRpcTests() =

  [<Fact>]
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

