namespace NLoop.Server.Tests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open LnClientDotnet
open Xunit

[<AutoOpen>]
module private ClightningClientTestHelpers =
  let utf8 = UTF8Encoding.UTF8

  let inline flatten (s: string) =
    s |> JsonSerializer.Deserialize<JsonDocument> |> JsonSerializer.Serialize

type PluginTests() =
  [<Fact>]
  member  this.PluginCanRun() =
      Environment.SetEnvironmentVariable("LIGHTNINGD_PLUGIN", "1")
      let dummyInit = $"""
  {{
    "id": 0,
    "method": "init",
    "params": {{
      "options": [{{
        "greeting": "World",
        "number": [0]
      }}],
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
      use cts = new CancellationTokenSource()
      use inMem = new MemoryStream(initB)
      use inStream = new StreamReader(inMem)
      use outMem = new MemoryStream(Array.zeroCreate 1024)
      let outStream = new StreamWriter(outMem)
      let t =
        Task.Run(fun () ->
        let methodHandler _methodArg =
          JsonSerializer.Deserialize<JsonElement> "{ \"test1\": \"ok\" }"
        Plugin.empty
        |> Plugin.setMockStdIn(inStream)
        |> Plugin.setMockStdOut(outStream)
        |> Plugin.addMethodWithNoDescription
          "testmethod"  methodHandler
        |> Plugin.runWithCancellation cts.Token
      )
      let result = outMem.ToArray() |> utf8.GetString
      printfn $"result: {result}"
      t.GetAwaiter().GetResult()
      ()
