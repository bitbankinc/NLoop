module ServerIntegrationTests

open System
open System.Collections.Generic
open System.IO
open System.Linq
open System.Net.Http
open System.Reflection
open System.Threading.Tasks
open BTCPayServer.Lightning
open DotNetLightning.Utils
open NBitcoin
open NBitcoin.Crypto
open NLoop.Infrastructure.DTOs
open NLoop.Infrastructure
open NLoop.Server.Services
open NLoop.Server
open NLoop.Server.Tests.Extensions
open Xunit
open FSharp.Control.Tasks

open Xunit.Abstractions
open DockerComposeFixture

let pairId = (Bitcoin.Instance :> INetworkSet, Bitcoin.Instance :> INetworkSet)

type ServerIntegrationTestsBase(msgSync: IMessageSink) =
  inherit DockerFixture(msgSync)


type ServerIntegrationTestsClass(dockerFixture: DockerFixture, output: ITestOutputHelper) =
  let testName =
    output
      .GetType()
      .GetField("test", BindingFlags.Instance ||| BindingFlags.NonPublic)
      .GetValue(output)
      :?> ITest
      |> fun test ->
      test.TestCase.TestMethod.Method.Name.Replace(" ", "_")
  let cli = dockerFixture.StartFixture(testName)

  interface IClassFixture<DockerFixture>

  [<Fact>]
  [<Trait("Docker", "Docker")>]
  member this.``BoltzClient tests (CreateSwap)`` () = task {
      let b = cli.Server.Boltz
      let! e = Assert.ThrowsAsync<HttpRequestException>(Func<Task>(fun () -> b.GetSwapTransactionAsync("Foo") :> Task))
      Assert.Contains("could not find swap with id", e.Message)

      let lndC = cli.User.Lnd :> ILightningClient

      // --- create swap ---
      let refundKey = new Key()
      let invoiceAmt = 100000m
      let! invoice =
        lndC.CreateInvoice(amount=(LNMoney.Satoshis invoiceAmt).ToLightMoney(), description="test", expiry=TimeSpan.FromMinutes(5.))
      let! resp =
        let channelOpenReq =  { ChannelOpenRequest.Private = true
                                InboundLiquidity = 50.
                                Auto = true }
        b.CreateSwapAsync({ PairId = pairId
                            OrderSide = OrderType.buy
                            RefundPublicKey = refundKey.PubKey
                            Invoice = invoice.ToDNLInvoice() }, channelOpenReq)

      Assert.NotNull(resp)
      Assert.NotNull(resp.Address)
      Assert.NotNull(resp.ExpectedAmount)
      Assert.NotNull(resp.TimeoutBlockHeight)
      // ------

      let! statusResp = b.GetSwapStatusAsync(resp.Id)
      Assert.Equal(SwapStatusType.InvoiceSet, statusResp.SwapStatus)
    }

  [<Fact>]
  [<Trait("Docker", "Docker")>]
  member this.``BoltzClientIsWorkingAgainstLitecoinD``() = task {
    let! a = cli.Litecoin.GetBlockchainInfoAsync()
    ()
  }

  (*
  [<Fact>]
  [<Trait("Docker", "Docker")>]
  member this.``BoltzClient tests (CreateReverseSwap)`` () = task {
      let b = getLocalBoltzClient()

      let preImage = RandomUtils.GetBytes(32)
      let preImageHash = preImage |> Hashes.SHA256 |> uint256
      let claimKey = new Key()
      let invoiceAmount = Money.Satoshis 100000m
      let! resp =
        b.CreateReverseSwapAsync({ CreateReverseSwapRequest.OrderSide = OrderType.buy
                                   PairId = pairId
                                   ClaimPublicKey = claimKey.PubKey
                                   InvoiceAmount = invoiceAmount
                                   PreimageHash = preImageHash })
      Assert.NotNull(resp)
      Assert.NotNull(resp.Invoice)
      Assert.NotNull(resp.LockupAddress)
      Assert.True(resp.OnchainAmount.Satoshi > 0L)
      Assert.True(resp.TimeoutBlockHeight.Value > 0u)

      let! statusResp = b.GetSwapStatusAsync(resp.Id)
      Assert.Equal(SwapStatusType.Created, statusResp.SwapStatus)

      // --- open channel and pay ---
      let lndC = getUserLndClient()
      let! nodesInfo = b.GetNodesAsync()
      let conn = nodesInfo.Nodes.["BTC"].Uris.First(fun uri -> uri.NodeId = nodesInfo.Nodes.["BTC"].NodeKey)
      let! _ = lndC.ConnectTo(conn.ToNodeInfo())
      let! fee = b.GetFeeEstimation()
      let! openChannelResp =
        let openChannelReq = OpenChannelRequest()
        openChannelReq.NodeInfo <- conn.ToNodeInfo()
        openChannelReq.ChannelAmount <- invoiceAmount * 2
        openChannelReq.FeeRate <- fee.["BTC"] |> decimal |> FeeRate
        lndC.OpenChannel(openChannelReq)

      let btcClient = getBTCClient()
      let! _ = btcClient.GenerateAsync(2)
      let! _ = btcClient.GenerateAsync(2)
      Assert.Equal(OpenChannelResult.Ok, openChannelResp.Result)

      // let payTask = lndC.Pay(resp.Invoice.ToString())

      // let! payResp = payTask
      // Assert.Equal(PayResult.Ok,  payResp.Result)
      // ---

      // let! txResp = b.GetSwapTransactionAsync(resp.Id)
      // Assert.NotNull(txResp.Transaction)
      // Assert.NotNull(txResp.TimeoutBlockHeight)

    }
    *)
