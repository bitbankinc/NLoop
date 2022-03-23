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
open NLoop.Domain
open NLoop.Server.DTOs
open NLoop.Server.RPCDTOs
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

  let inline getBareClientProxy() =
    let formatter =
      let f = new JsonMessageFormatter()
      f

    let pipe = clientPipe()
    let handler = new NewLineDelimitedMessageHandler(pipe, pipe, formatter)
    task {
      do! pipe.ConnectAsync()
      return new JsonRpc (handler)
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
  let sp =
    TestHelpers.GetTestServiceProvider(fun (serviceCollection: IServiceCollection) ->
      serviceCollection.AddSingleton<INLoopJsonRpcServer, NLoopJsonRpcServer>() |> ignore
    )
  let rpcServer = sp.GetService<INLoopJsonRpcServer>()

  let _serverTask = createRpcServer(rpcServer)

  [<Fact>]
  member this.ServerStreamTests_Info() =
    task {
      let! client = getClientProxy()
      let! resp =
        client.Version()
      Assert.NotNull(resp)
      let! info =
        client.Info()
      Assert.NotNull(info.Version)
      ()
    }

  [<Fact>]
  member this.ServerStreamTests_Autoloop() =
    let req = {
      SetLiquidityParametersRequest.Parameters =
        {
          LiquidityParameters.Rules = [|
            {
              ChannelId = chanId1 |> ValueSome
              PubKey = ValueNone
              Type = LiquidityRuleType.THRESHOLD
              IncomingThreshold = 25s<percent>
              OutgoingThreshold = 25s<percent>
            }
          |]
          FeePPM = 20L<ppm> |> ValueSome
          SweepFeeRateSatPerKVByte = ValueNone
          MaxSwapFeePpm = ValueNone
          MaxRoutingFeePpm = ValueNone
          MaxPrepayRoutingFeePpm = ValueNone
          MaxPrepay = ValueNone
          MaxMinerFee = ValueNone
          SweepConfTarget = 3
          FailureBackoffSecond = 600
          AutoLoop = false
          AutoMaxInFlight = 1
          MinSwapAmountLoopOut = None
          MaxSwapAmountLoopOut = None
          MinSwapAmountLoopIn = None
          MaxSwapAmountLoopIn = None
          OnChainAsset = SupportedCryptoCode.BTC |> ValueSome
          HTLCConfTarget = 3 |> Some
        }
    }
    task {
      let! client = getClientProxy()
      let dtoReq = req |> convertDTOToJsonRPCStyle
      let! _ = client.SetLiquidityParams(dtoReq, NLoopClient.CryptoCode.BTC)
      let! dtoResp = client.GetLiquidityParams(NLoopClient.CryptoCode.BTC)
      let resp = dtoResp |> convertDTOToNLoopCompatibleStyle
      Assert.Equal(req.Parameters, resp)
      ()
    }


(*
  [<Fact>]
  member this.ServerStreamTests_Plugin() =
    task {
      let! client = getBareClientProxy()
      let dto =
        let d = LightningInitConfigurationDTO()
        d
      // do! client.InvokeWithParameterObjectAsync()
      ()
    }

*)
