namespace NLoop.Server.Tests

open System
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils
open FSharp.Control
open FSharp.Control.Tasks

open LndClient
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open NBitcoin
open NBitcoin.Altcoins
open NBitcoin.DataEncoders
open NLoop.Server
open NLoop.Server.SwapServerClient
open NLoop.Server.SwapServerClient
open Xunit

open NLoop.Domain
open NLoop.Domain.IO
open NLoopClient
open NLoop.Server.DTOs
open NLoop.Server.Tests
open NLoop.Server.RPCDTOs

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

  let loopInReq =
    {
      LoopInRequest.Amount = swapAmount
      ChannelId = chanId1 |> Some
      Label = None
      PairId = None
      MaxMinerFee = ValueNone
      MaxSwapFee = ValueNone
      HtlcConfTarget = ValueNone
      LastHop = None
    }

  let channel1 = {
    ListChannelResponse.Id = chanId1
    Cap = Money.Satoshis(10000L)
    LocalBalance = Money.Satoshis(10000L)
    RemoteBalance = Money.Zero
    NodeId = peer1
  }

  let channel2 = {
    ListChannelResponse.Id = chanId2
    Cap = Money.Satoshis(10000L)
    LocalBalance = Money.Satoshis(10000L)
    RemoteBalance = Money.Zero
    NodeId = peer1
  }
  let node1Uri = {
    PeerConnectionString.EndPoint = IPEndPoint(IPAddress.Loopback, 6028)
    NodeId = peer1
  }
  let node1Info =
    Map.ofSeq
      [
        (SupportedCryptoCode.BTC.ToString(),{ SwapDTO.NodeInfo.Uris = [|node1Uri|] ; SwapDTO.NodeInfo.NodeKey = peer1 })
      ]

  let routeToNode1 =
    let hop1 = {
      RouteHop.Fee = LNMoney.Satoshis(10L)
      PubKey = peer1
      ShortChannelId = chanId1
      CLTVExpiryDelta = 20u
    }
    (peer1 |> NodeId, Route([hop1]))

  let routeToNode2 =
    let hop1 = {
      RouteHop.Fee = LNMoney.Satoshis(10L)
      PubKey = peer2
      ShortChannelId = chanId2
      CLTVExpiryDelta = 20u
    }
    (peer2 |> NodeId, Route([hop1]))
  let testBlockchainInfo = {
    BlockChainInfo.Height = BlockHeight.Zero
    Progress = 1.0f
    BestBlockHash = Network.RegTest.GenesisHash
  }
  let testLoopOutQuote = {
    SwapDTO.LoopOutQuote.SwapFee = Money.Satoshis(5L)
    SwapDTO.LoopOutQuote.SweepMinerFee = Money.Satoshis(1L)
    SwapDTO.LoopOutQuote.SwapPaymentDest = peer1
    SwapDTO.LoopOutQuote.CltvDelta = BlockHeightOffset32(20u)
    SwapDTO.LoopOutQuote.PrepayAmount = Money.Satoshis(50L)
  }

  let testLoopInQuote = {
    SwapDTO.LoopInQuote.MinerFee = Money.Satoshis(5L)
    SwapDTO.LoopInQuote.SwapFee = Money.Satoshis(1L)
  }

  let hex = HexEncoder()
  let getDummyTestInvoice maybeAmount (paymentPreimage: PaymentPreimage) (network: Network) =
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
  let reverseSwapRedeem = getReverseSwapScript preimage.Hash claimKey.PubKey
  let loopOutResp1 =
    {
      SwapDTO.LoopOutResponse.Id = swapId
      SwapDTO.LoopOutResponse.LockupAddress = reverseSwapRedeem.WitHash.GetAddress(Network.RegTest).ToString()
      SwapDTO.LoopOutResponse.Invoice =
        invoice
      SwapDTO.LoopOutResponse.TimeoutBlockHeight = BlockHeight(30u)
      SwapDTO.LoopOutResponse.OnchainAmount = swapAmount
      SwapDTO.LoopOutResponse.RedeemScript = reverseSwapRedeem
      SwapDTO.LoopOutResponse.MinerFeeInvoice = None
    }

  let refundKey = new Key()
  let theirClaimKey = new Key()
  let swapRedeem = Scripts.swapScriptV1 preimage.Hash theirClaimKey.PubKey refundKey.PubKey (BlockHeight 30u)
  let loopInResp1 =
    {
      SwapDTO.LoopInResponse.Id = swapId
      SwapDTO.LoopInResponse.Address = swapRedeem.WitHash.GetAddress(Network.RegTest).ToString()
      SwapDTO.LoopInResponse.RedeemScript = swapRedeem
      SwapDTO.LoopInResponse.AcceptZeroConf = false
      SwapDTO.LoopInResponse.ExpectedAmount = swapAmount
      SwapDTO.LoopInResponse.TimeoutBlockHeight = BlockHeight(30u)
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
    member this.KeepPublishDummyLoopInEvent() =
      task {
        while true do
          do! Task.Delay(10)
          this.Publish({
            Swap.EventWithId.Id = SwapId.SwapId(swapId)
            Swap.EventWithId.Event = Swap.Event.NewLoopInAdded(Unchecked.defaultof<_>)
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
      ("valid request", [channel1], chan1Rec, node1Info, Map.ofSeq [routeToNode1], loopOutResp1, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.OK, None)
      ("valid request with no channel specified", [channel1], { chan1Rec with ChannelIds = ValueNone } , node1Info, Map.ofSeq [routeToNode1], loopOutResp1, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.OK, None)
      ("valid request with no channel specified 2", [channel1], { chan1Rec with ChannelIds = ValueSome([||]) } , node1Info, Map.ofSeq [routeToNode1], loopOutResp1, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.OK, None)
      ("We have a channel but that is different from the one specified in request", [channel1], {chan1Rec with ChannelIds = ValueSome([| chanId2 |])}, node1Info, Map.ofSeq [routeToNode2], loopOutResp1, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.BadRequest, Some "does not exist")
      ("one of the channel user specified does not exist", [channel1], {chan1Rec with ChannelIds = ValueSome([| chanId1; chanId2 |])}, node1Info, Map.ofSeq [routeToNode2], loopOutResp1, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.BadRequest, Some "does not exist")
      let inprogressBlockchainInfo = {
        testBlockchainInfo
          with
            Progress = 0.99f
      }
      ("Blockchain is not synced yet", [channel1], chan1Rec, node1Info, Map.ofSeq [routeToNode1], loopOutResp1, inprogressBlockchainInfo,testLoopOutQuote, HttpStatusCode.ServiceUnavailable, Some("blockchain is not synced"))
      ("no routes to the node", [channel1], chan1Rec, node1Info, Map.empty, loopOutResp1, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.ServiceUnavailable, Some(""))

      let quote = {
        testLoopOutQuote
          with
          SwapFee = Money.Satoshis(10000L)
      }
      let req = {
        chan1Rec
          with
          MaxSwapFee = ValueSome <| Money.Satoshis(9999L)
      }
      ("They required expensive swap fee in loop-out quote", [channel1], req, node1Info, Map.ofSeq [routeToNode1], loopOutResp1, testBlockchainInfo, quote, HttpStatusCode.BadRequest, Some("Swap fee specified by the server is too high"))
      let quote = {
        testLoopOutQuote
          with
            PrepayAmount = Money.Satoshis(100L)
      }
      let req = {
        chan1Rec
          with
          MaxPrepayAmount = ValueSome <| Money.Satoshis(99L)
      }
      ("prepay miner fee too expensive", [channel1], req, node1Info, Map.ofSeq [routeToNode1], loopOutResp1, testBlockchainInfo, quote, HttpStatusCode.BadRequest, Some("prepay fee specified by the server is too high"))

      let req = {
        chan1Rec
          with
          Address = Some "foo"
      }
      ("invalid address in the request from an user (bogus)", [channel1], req, node1Info, Map.ofSeq [routeToNode1], loopOutResp1, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.BadRequest, Some "Invalid address")
      let req = {
        chan1Rec
          with
          Address =
            new Key(hex.DecodeData("9797979797979797979797979797979797979797979797979797979797979797"))
            |> fun k -> k.PubKey.WitHash.GetAddress(Network.Main).ToString() |> Some
      }
      ("invalid address in the request from an user (network mismatch)", [channel1], req, node1Info, Map.ofSeq [routeToNode1], loopOutResp1, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.BadRequest, Some "Invalid address")
      let req = {
        chan1Rec
          with
          Address =
            new Key(hex.DecodeData("9797979797979797979797979797979797979797979797979797979797979797"))
            |> fun k -> k.PubKey.WitHash.GetAddress(Litecoin.Instance.Regtest).ToString() |> Some
      }
      ("invalid address in the request from an user (cryptocode mismatch)", [channel1], req, node1Info, Map.ofSeq [routeToNode1], loopOutResp1, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.BadRequest, Some "Invalid address")
      let resp = {
        loopOutResp1
          with
          LockupAddress = "foo"
      }
      ("Invalid address from the server (bogus)", [channel1], chan1Rec, node1Info, Map.ofSeq [routeToNode1], resp, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.InternalServerError, Some("Boltz returned invalid bitcoin address for lockup address"))
      let resp = {
        loopOutResp1
          with
          LockupAddress = reverseSwapRedeem.WitHash.GetAddress(Network.Main).ToString()
      }
      ("Invalid address from the server (network mismatch)", [channel1], chan1Rec, node1Info, Map.ofSeq [routeToNode1], resp, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.InternalServerError, Some("Boltz returned invalid bitcoin address for lockup address"))
      let resp = {
        loopOutResp1
          with
          LockupAddress = reverseSwapRedeem.WitHash.GetAddress(Litecoin.Instance.Regtest).ToString()
      }
      ("Invalid address from the server (cryptocode mismatch)", [channel1], chan1Rec, node1Info, Map.ofSeq [routeToNode1], resp, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.InternalServerError, Some("Boltz returned invalid bitcoin address for lockup address"))

      let err = "Payment Hash in invoice does not match preimage hash we specified in request"
      let resp = {
        loopOutResp1
          with
          SwapDTO.LoopOutResponse.Invoice = getDummyTestInvoice (swapAmount.Satoshi |> LNMoney.Satoshis |> Some) (PaymentPreimage.Create(Array.zeroCreate 32)) Network.RegTest
      }
      ("Invalid payment request (invalid preimage)", [channel1], chan1Rec, node1Info, Map.ofSeq [routeToNode1], resp, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.InternalServerError, Some(err))
      let resp = {
        loopOutResp1
          with
          SwapDTO.LoopOutResponse.Invoice = getDummyTestInvoice ((swapAmount.Satoshi - 1L) |> LNMoney.Satoshis |> Some) preimage Network.RegTest
      }
      ("Invalid payment request (invalid amount)", [channel1], chan1Rec, node1Info, Map.ofSeq [routeToNode1], resp, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.InternalServerError, None)
      let resp = {
        loopOutResp1
          with
          SwapDTO.LoopOutResponse.Invoice = getDummyTestInvoice None preimage Network.RegTest
      }
      ("Valid payment request (no amount)", [channel1], chan1Rec, node1Info, Map.ofSeq [routeToNode1], resp, testBlockchainInfo, testLoopOutQuote, HttpStatusCode.OK, None)
    ]
    |> Seq.map(fun (name,
                    channels: ListChannelResponse list,
                    req: LoopOutRequest,
                    swapServerNodes: Map<string, SwapDTO.NodeInfo>,
                    routesToNodes: Map<NodeId, Route>,
                    responseFromServer: SwapDTO.LoopOutResponse,
                    blockchainInfo: BlockChainInfo,
                    quote: SwapDTO.LoopOutQuote,
                    expectedStatusCode: HttpStatusCode,
                    expectedErrorMsg: string option) -> [|
      name |> box
      channels |> box
      req |> box
      swapServerNodes |> box
      routesToNodes |> box
      responseFromServer |> box
      blockchainInfo |> box
      quote |> box
      expectedStatusCode |> box
      expectedErrorMsg |> box
    |])

  [<Theory>]
  [<MemberData(nameof(ServerAPITest.TestValidateLoopOutData))>]
  member this.TestValidateLoopOut(_name: string,
                                  channels: ListChannelResponse list,
                                  loopOutReq: LoopOutRequest,
                                  swapServerNodes: Map<string, SwapDTO.NodeInfo>,
                                  routesToNodes: Map<NodeId, Route>,
                                  responseFromServer: SwapDTO.LoopOutResponse,
                                  blockchainInfo: BlockChainInfo,
                                  quote: SwapDTO.LoopOutQuote,
                                  expectedStatusCode: HttpStatusCode,
                                  expectedErrorMsg: string option) = task {
    use server = new TestServer(TestHelpers.GetTestHost(fun (sp: IServiceCollection) ->
        let lnClientParam = {
          DummyLnClientParameters.Default
            with
            ListChannels = channels
            QueryRoutes = fun pk _amount maybeChanIdSpecified ->
              match routesToNodes.TryGetValue (pk |> NodeId), maybeChanIdSpecified with
              | (true, v), None ->
                v
              | (true, v), Some chanId when not <| v.Value.IsEmpty && v.Value.Head.ShortChannelId = chanId ->
                v
              | _ -> Route([])
        }
        sp
          .AddSingleton<ILoggerFactory, LoggerFactory>()
          .AddSingleton<ISwapActor>(TestHelpers.GetDummySwapActor())
          .AddSingleton<INLoopLightningClient>(TestHelpers.GetDummyLightningClient(lnClientParam))
          .AddSingleton<ILightningClientProvider>(TestHelpers.GetDummyLightningClientProvider(lnClientParam))
          .AddSingleton<GetBlockchainClient>(Func<IServiceProvider, _> (fun sp _cc ->
            TestHelpers.GetDummyBlockchainClient( {
              DummyBlockChainClientParameters.Default with GetBlockchainInfo = fun () -> blockchainInfo
            })
          ))
          .AddSingleton<GetSwapKey>(Func<IServiceProvider, _> (fun _ () -> claimKey |> Task.FromResult))
          .AddSingleton<GetSwapPreimage>(Func<IServiceProvider, _> (fun _ () -> preimage |> Task.FromResult))
          .AddSingleton<ISwapServerClient>(TestHelpers.GetDummySwapServerClient({
            DummySwapServerClientParameters.Default
              with
                LoopOutQuote =  fun _req -> quote |> Ok |> Task.FromResult
                GetNodes = fun () -> { Nodes = swapServerNodes }
                LoopOut = fun _req -> responseFromServer |> Task.FromResult
          }))
          |> ignore
      ))

    let opts =
      let o = JsonSerializerOptions(IgnoreNullValues = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
      o.AddNLoopJsonConverters()
      o
    let client = server.CreateClient()
    use e =
      server.Services.GetRequiredService<IEventAggregator>()
    let _ = e.KeepPublishDummyLoopOutEvent()
    let! resp =
      let content = JsonContent.Create(loopOutReq, Unchecked.defaultof<_>, opts)
      client.PostAsync("/v1/loop/out", content)
    Assert.Equal(expectedStatusCode, resp.StatusCode)
    let! msg = resp.Content.ReadAsStringAsync()
    expectedErrorMsg |> Option.iter(fun expected -> Assert.Contains(expected, msg))
  }

  static member TestValidateLoopInData =
    seq [
      ("loop in success", loopInReq, testLoopInQuote, loopInResp1, testBlockchainInfo, HttpStatusCode.OK, None)
      ("blockchain not synced", loopInReq, testLoopInQuote, loopInResp1, { testBlockchainInfo with Progress = 0.999f }, HttpStatusCode.ServiceUnavailable, Some("blockchain is not synced"))
      let q = {
        testLoopInQuote
          with
          MinerFee = Money.Satoshis 1000L
      }
      let req = {
        loopInReq
          with
          MaxMinerFee = Money.Satoshis 999L |> ValueSome
      }
      ("specified miner fee too high", req, q, loopInResp1, testBlockchainInfo, HttpStatusCode.BadRequest, Some "Miner fee specified by the server is too high")
      let q = {
        testLoopInQuote
          with
          SwapFee = Money.Satoshis 1000L
      }
      let req = {
        loopInReq
          with
          MaxSwapFee = Money.Satoshis 999L |> ValueSome
      }
      ("swap fee too high", req, q, loopInResp1, testBlockchainInfo, HttpStatusCode.BadRequest, Some "Swap fee specified by the server is too high")
      ("on-chain miner fee too high", { loopInReq with MaxMinerFee = ValueSome(Money.Satoshis 10L) }, testLoopInQuote, loopInResp1, testBlockchainInfo, HttpStatusCode.InternalServerError, Some $"OnChain FeeRate is too high")
    ]
    |> Seq.map(fun (name,
                    req,
                    quote: SwapDTO.LoopInQuote,
                    responseFromServer: SwapDTO.LoopInResponse,
                    blockchainInfo,
                    expectedStatusCode: HttpStatusCode,
                    expectedErrorMsg: string option
                    ) ->
    [|
      name |> box
      req |> box
      quote |> box
      responseFromServer |> box
      blockchainInfo |> box
      expectedStatusCode |> box
      expectedErrorMsg |> box
    |])

  [<Theory>]
  [<MemberData(nameof(ServerAPITest.TestValidateLoopInData))>]
  member this.TestValidateLoopIn(_name: string,
                                 req: LoopInRequest,
                                 quote: SwapDTO.LoopInQuote,
                                 responseFromServer: SwapDTO.LoopInResponse,
                                 blockchainInfo: BlockChainInfo,
                                 expectedStatusCode: HttpStatusCode,
                                 expectedErrorMsg: string option
                                 ) =
    task {
      use server = new TestServer(TestHelpers.GetTestHost(fun (sp: IServiceCollection) ->
        let lnClientParam =
          let invoice = getDummyTestInvoice (Some (swapAmount.ToLNMoney())) preimage Network.RegTest
          {
            DummyLnClientParameters.Default
              with
              GetInvoice = fun _preimage _amt _ _ _ -> invoice
              SubscribeSingleInvoice = fun _hash -> asyncSeq {
                yield {
                  IncomingInvoiceSubscription.PaymentRequest = invoice
                  AmountPayed = swapAmount.ToLNMoney()
                  InvoiceState = IncomingInvoiceStateUnion.Settled
                }
              }
          }
        let walletClientParams = {
          DummyWalletClientParameters.Default
            with
            GetSendingTxFee = fun _destinations _conf -> Ok(Money.Satoshis 20L)
        }
        sp
          .AddSingleton<ISwapActor>(TestHelpers.GetDummySwapActor())
          .AddSingleton<INLoopLightningClient>(TestHelpers.GetDummyLightningClient(lnClientParam))
          .AddSingleton<GetWalletClient>(Func<IServiceProvider, _>(fun _sp _ ->
            TestHelpers.GetDummyWalletClient(walletClientParams)
          ))
          .AddSingleton<ILightningClientProvider>(TestHelpers.GetDummyLightningClientProvider(lnClientParam))
            .AddSingleton<ISwapActor>(TestHelpers.GetDummySwapActor())
            .AddSingleton<GetBlockchainClient>(Func<IServiceProvider, _> (fun sp _cc ->
              TestHelpers.GetDummyBlockchainClient( {
                DummyBlockChainClientParameters.Default with GetBlockchainInfo = fun () -> blockchainInfo
              })
            ))
          .AddSingleton<GetSwapKey>(Func<IServiceProvider, _> (fun _ () -> refundKey |> Task.FromResult))
          .AddSingleton<GetSwapPreimage>(Func<IServiceProvider, _> (fun _ () -> preimage |> Task.FromResult))
          .AddSingleton<ISwapServerClient>(TestHelpers.GetDummySwapServerClient({
            DummySwapServerClientParameters.Default
              with
                LoopInQuote =  fun _req -> quote |> Ok |> Task.FromResult
                LoopIn = fun _req -> responseFromServer |> Task.FromResult
          }))
          |> ignore
        ))

      let opts =
        let o = JsonSerializerOptions(IgnoreNullValues = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
        o.AddNLoopJsonConverters()
        o
      let client = server.CreateClient()
      use e =
        server.Services.GetRequiredService<IEventAggregator>()
      let _ = e.KeepPublishDummyLoopInEvent()
      let! resp =
        let content = JsonContent.Create(req, Unchecked.defaultof<_>, opts)
        client.PostAsync("/v1/loop/in", content)
      let! msg = resp.Content.ReadAsStringAsync()
      Assert.Equal(expectedStatusCode, resp.StatusCode)
      expectedErrorMsg |> Option.iter(fun expected -> Assert.Contains(expected, msg))
    }


  static member TestAutoLoopAPIData =
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
    seq [
      ("success", req, [channel1], HttpStatusCode.OK, None)
      ("no channels exist", req, [], HttpStatusCode.BadRequest, Some "Channel .+ does not exist")
      ("Channel specified does not exist", req, [channel2], HttpStatusCode.BadRequest, Some "Channel .+ does not exist")
    ]
    |> Seq.map(fun (n: string,
                    req,
                    channels: ListChannelResponse list,
                    expectedStatusCode: HttpStatusCode,
                    expectedErrorMsg: string option) -> [|
      n |> box
      req |> box
      channels |> box
      expectedStatusCode |> box
      expectedErrorMsg |> box
    |])
  [<Theory>]
  [<MemberData(nameof(ServerAPITest.TestAutoLoopAPIData))>]
  member this.TestAutoLoopAPI(_name: string,
                              req: SetLiquidityParametersRequest,
                              channels: ListChannelResponse list,
                              expectedStatusCode: HttpStatusCode,
                              expectedErrorMsg: string option) =
    task {
      use server = new TestServer(TestHelpers.GetTestHost(fun (sp: IServiceCollection) ->
          let lnClientParam = {
            DummyLnClientParameters.Default
              with
              ListChannels = channels
          }
          sp
            .AddSingleton<INLoopLightningClient>(TestHelpers.GetDummyLightningClient(lnClientParam))
            .AddSingleton<ILightningClientProvider>(TestHelpers.GetDummyLightningClientProvider(lnClientParam))
            |> ignore
          ()
        ))

      let client = server.CreateClient()
      let! resp =
        let opts =
          let o = JsonSerializerOptions(IgnoreNullValues = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
          o.AddNLoopJsonConverters()
          o
        let content = JsonContent.Create(req, Unchecked.defaultof<_>, opts)
        client.PostAsync("/v1/liquidity/params", content)
      let! msg = resp.Content.ReadAsStringAsync()
      Assert.Equal(expectedStatusCode, resp.StatusCode)
      expectedErrorMsg |> Option.iter(fun expected ->
        Regex.IsMatch(msg, expected)
        |> Assert.True
      )
      ()
    }
