namespace NLoop.Server.Tests

open System
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Net.Sockets
open System.Text.Json
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils
open FSharp.Control.Tasks

open LndClient
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open NBitcoin
open NBitcoin.DataEncoders
open NLoop.Server
open NLoop.Server.SwapServerClient
open Xunit

open NLoop.Domain
open NLoop.Domain.IO
open NLoopClient
open NLoop.Server.DTOs
open NLoop.Server.Tests

[<AutoOpen>]
module private ServerAPITestHelpers =
  let swapAmount = Money.Satoshis(7500L)
  let peer1 = PubKey("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619")
  let peer2 = PubKey("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c")
  let chanId1 = ShortChannelId.FromUInt64(1UL)
  let chanId2 = ShortChannelId.FromUInt64(2UL)
  let chan1Rec =
    {
      LoopOutRequest.Amount = swapAmount
      ChannelIds = [| chanId1 |] |> ValueSome
      Address = None
      PairId = None
      SwapTxConfRequirement = None
      Label = None
      MaxSwapRoutingFee =  ValueNone
      MaxPrepayRoutingFee = ValueNone
      MaxSwapFee = ValueNone
      MaxPrepayAmount = ValueNone
      MaxMinerFee = ValueNone
      SweepConfTarget = ValueNone
    }


  let channel1 = {
    LndClient.ListChannelResponse.Id = chanId1
    Cap = Money.Satoshis(10000L)
    LocalBalance = Money.Satoshis(10000L)
    NodeId = peer1
  }

  let channel2 = {
    LndClient.ListChannelResponse.Id = chanId2
    Cap = Money.Satoshis(10000L)
    LocalBalance = Money.Satoshis(10000L)
    NodeId = peer1
  }
  let node1Uri = {
    PeerConnectionString.EndPoint = IPEndPoint(IPAddress.Loopback, 6028)
    NodeId = peer1
  }
  let node1Info = ("node1", { SwapDTO.NodeInfo.Uris = [|node1Uri|] ; SwapDTO.NodeInfo.NodeKey = peer1 })

  let routeToNode1 =
    let hop1 = {
      RouteHop.Fee = LNMoney.Satoshis(10L)
      PubKey = peer1
      ShortChannelId = chanId1
      CLTVExpiryDelta = 20u
    }
    (peer1 |> NodeId, Route([hop1]))

  let testQuote = {
    SwapDTO.LoopOutQuote.SwapFee = Money.Satoshis(5L)
    SwapDTO.LoopOutQuote.SweepMinerFee = Money.Satoshis(1L)
    SwapDTO.LoopOutQuote.SwapPaymentDest = peer1
    SwapDTO.LoopOutQuote.CltvDelta = BlockHeightOffset32(20u)
    SwapDTO.LoopOutQuote.PrepayAmount = Money.Satoshis(50L) }
  let hex = HexEncoder()
  let getDummyTestInvoice (maybeAmount) (paymentPreimage: PaymentPreimage) (network: Network) =
    assert(network <> null)
    let paymentHash = paymentPreimage.Hash
    let fields = { TaggedFields.Fields = [ PaymentHashTaggedField paymentHash; DescriptionTaggedField "test" ] }
    PaymentRequest.TryCreate(network, maybeAmount, DateTimeOffset.UtcNow, fields, new Key())
    |> ResultUtils.Result.deref
  let preimage = PaymentPreimage.Create(RandomUtils.GetBytes(32))
  let swapId = "foo"
  let claimKey = new Key()
  let invoice = getDummyTestInvoice (Some <| swapAmount.ToLNMoney()) preimage Network.RegTest
  let getReverseSwapScript (preimageHash: PaymentHash) (claimPubKey: PubKey)  =
    $"OP_SIZE 20 OP_EQUAL OP_IF OP_HASH160 %s{preimageHash.GetRIPEMD160() |> hex.EncodeData} OP_EQUALVERIFY {claimPubKey.ToHex()} OP_ELSE OP_DROP 1e OP_CLTV OP_DROP 030ae596d7be77f8c4dca8c9aa53fe4a67469b1977f17a475803c2cf76256dccbd OP_ENDIF OP_CHECKSIG"
    |> Script
  let redeem = getReverseSwapScript preimage.Hash claimKey.PubKey
  let loopOutResp1 =
    {
      SwapDTO.LoopOutResponse.Id = swapId
      SwapDTO.LoopOutResponse.LockupAddress = redeem.WitHash.GetAddress(Network.RegTest).ToString()
      SwapDTO.LoopOutResponse.Invoice =
        invoice
      SwapDTO.LoopOutResponse.TimeoutBlockHeight = BlockHeight(30u)
      SwapDTO.LoopOutResponse.OnchainAmount = swapAmount
      SwapDTO.LoopOutResponse.RedeemScript = redeem
      SwapDTO.LoopOutResponse.MinerFeeInvoice = None
    }
  type IEventAggregator with
    member this.KeepPublishDummyLoopOutEvent() =
      task {
        while true do
          do! Task.Delay(10)
          this.Publish({
            Swap.EventWithId.Id = SwapId.SwapId(swapId)
            Swap.EventWithId.Event = Swap.Event.NewLoopOutAdded(Unchecked.defaultof<_>)
          })
      }
type ServerAPITest() =
  [<Fact>]
  [<Trait("Docker", "Off")>]
  member this.``ServerTest(getversion)`` () = task {
    use server = new TestServer(TestHelpers.GetTestHost())
    use httpClient = server.CreateClient()
    let! resp =
      new HttpRequestMessage(HttpMethod.Get, "/v1/version")
      |> httpClient.SendAsync

    let! str = resp.Content.ReadAsStringAsync()
    Assert.Equal(4, str.Split(".").Length)

    let cli = NLoopClient(httpClient)
    cli.BaseUrl <- "http://localhost"
    let! v = cli.VersionAsync()
    Assert.NotEmpty(v)
    Assert.Equal(v.Split(".").Length, 4)
  }

  static member TestValidateLoopOutData =
    [
      ("mainnet address with mainnet backend", [channel1], chan1Rec, Map.ofSeq[node1Info], Map.ofSeq[routeToNode1], loopOutResp1, HttpStatusCode.OK, None)
      ("no connected nodes", [channel1], chan1Rec, Map.empty, Map.empty, loopOutResp1, HttpStatusCode.ServiceUnavailable, Some(""))
      ("no routes to the nodes", [channel1], chan1Rec, Map.ofSeq[node1Info], Map.empty, loopOutResp1, HttpStatusCode.ServiceUnavailable, Some(""))

      let resp = {
        loopOutResp1
          with
          LockupAddress = "foo"
      }
      ("Invalid address", [channel1], chan1Rec, Map.ofSeq[node1Info], Map.ofSeq[routeToNode1], resp, HttpStatusCode.ServiceUnavailable, Some("Boltz returned invalid bitcoin address for lockup address"))

      let err = "Payment Hash in invoice does not match preimage hash we specified in request"
      let resp = {
        loopOutResp1
          with
          SwapDTO.LoopOutResponse.Invoice = getDummyTestInvoice (swapAmount.Satoshi |> LNMoney.Satoshis |> Some) (PaymentPreimage.Create(Array.zeroCreate 32)) (Network.RegTest)
      }
      ("Invalid payment request (invalid preimage)", [channel1], chan1Rec, Map.ofSeq[node1Info], Map.ofSeq[routeToNode1], resp, HttpStatusCode.ServiceUnavailable, Some(err))
      let resp = {
        loopOutResp1
          with
          SwapDTO.LoopOutResponse.Invoice = getDummyTestInvoice ((swapAmount.Satoshi - 1L) |> LNMoney.Satoshis |> Some) (preimage) (Network.RegTest)
      }
      ("Invalid payment request (invalid amount)", [channel1], chan1Rec, Map.ofSeq[node1Info], Map.ofSeq[routeToNode1], resp, HttpStatusCode.ServiceUnavailable, None)
      let resp = {
        loopOutResp1
          with
          SwapDTO.LoopOutResponse.Invoice = getDummyTestInvoice (None) (preimage) (Network.RegTest)
      }
      ("Valid payment request (no amount)", [channel1], chan1Rec, Map.ofSeq[node1Info], Map.ofSeq[routeToNode1], resp, HttpStatusCode.OK, None)
      ()
    ]
    |> Seq.map(fun (name,
                    channels,
                    req,
                    swapServerNodes: Map<string, SwapDTO.NodeInfo>,
                    routesToNodes: Map<NodeId, Route>,
                    responseFromServer: SwapDTO.LoopOutResponse,
                    expectedStatusCode,
                    expectedErrorMsg: string option) -> [|
      name |> box
      channels |> box
      req |> box
      swapServerNodes |> box
      routesToNodes |> box
      responseFromServer |> box
      expectedStatusCode |> box
      expectedErrorMsg |> box
    |])

  [<Theory>]
  [<MemberData(nameof(ServerAPITest.TestValidateLoopOutData))>]
  member this.TestValidateLoopOut(_name,
                                  channels,
                                  loopOutReq: LoopOutRequest,
                                  swapServerNodes: Map<string, SwapDTO.NodeInfo>,
                                  routesToNodes: Map<NodeId, Route>,
                                  responseFromServer: SwapDTO.LoopOutResponse,
                                  expectedStatusCode: HttpStatusCode,
                                  expectedErrorMsg: string option) = task {
    use server = new TestServer(TestHelpers.GetTestHost(fun (sp: IServiceCollection) ->
        let lnClientParam = {
          DummyLnClientParameters.ListChannels = channels
          QueryRoutes = fun pk _amount -> routesToNodes.[pk |> NodeId]
        }
        sp
          .AddSingleton<ISwapActor>(TestHelpers.GetDummySwapActor())
          .AddSingleton<INLoopLightningClient>(TestHelpers.GetDummyLightningClient(lnClientParam))
          .AddSingleton<ILightningClientProvider>(TestHelpers.GetDummyLightningClientProvider(lnClientParam))
          .AddSingleton<GetBlockchainClient>(Func<IServiceProvider, _> (fun sp cc ->
            let blockchainInfo = {
              BlockChainInfo.Height = BlockHeight.Zero
              Progress = 1.0f
              BestBlockHash = Network.RegTest.GenesisHash
            }
            TestHelpers.GetDummyBlockchainClient( {
              DummyBlockChainClientParameters.Default with GetBlockchainInfo = fun () -> blockchainInfo
            })
          ))
          .AddSingleton<GetSwapKey>(Func<IServiceProvider, _> (fun _ () -> claimKey |> Task.FromResult))
          .AddSingleton<GetSwapPreimage>(Func<IServiceProvider, _> (fun _ () -> preimage |> Task.FromResult))
          .AddSingleton<ISwapServerClient>(TestHelpers.GetDummySwapServerClient({
            DummySwapServerClientParameters.Default
              with
                LoopOutQuote =  fun req -> testQuote
                GetNodes = fun () -> { Nodes = swapServerNodes }
                LoopOut = fun _req -> responseFromServer
          }))
          |> ignore
      ))

    let opts = JsonSerializerOptions(IgnoreNullValues = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    opts.AddNLoopJsonConverters(Network.RegTest)
    let client = server.CreateClient()
    let _ =
      server.Services.GetRequiredService<IEventAggregator>().KeepPublishDummyLoopOutEvent()
    let respT =
      let content = JsonContent.Create(loopOutReq, Unchecked.defaultof<_>, opts)
      client.PostAsync("/v1/loop/out", content)
    let! resp = respT
    let! msg = resp.Content.ReadAsStringAsync()
    Assert.Equal(expectedStatusCode, resp.StatusCode)
    expectedErrorMsg |> Option.iter(fun expected -> Assert.Contains(expected, msg))
    ()
  }
