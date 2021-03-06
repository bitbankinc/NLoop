module Tests

open DotNetLightning.Utils
open Helpers
open System
open System.Net.Http
open System.Threading.Tasks
open BTCPayServer.Lightning
open NBitcoin
open NBitcoin.Crypto
open NLoop.Infrastructure.DTOs
open NLoop.Server
open NLoop.Server.Services
open Xunit
open FSharp.Control.Tasks

[<Fact>]
let ``BoltzClient tests (GetVersion)`` () = task {
    let b = BoltzClient("https://testnet.boltz.exchange/api/", Network.TestNet)
    let! v = b.GetVersionAsync()
    Assert.NotNull(v.Version)
  }

[<Fact>]
let ``BoltzClient tests (GetPairs)`` () = task {
    let b = BoltzClient("https://testnet.boltz.exchange/api/", Network.TestNet)
    let! p = b.GetPairsAsync()
    Assert.NotEmpty(p.Pairs)
  }

[<Fact>]
let ``BoltzClient tests (GetNodes)`` () = task {
    let b = BoltzClient("https://testnet.boltz.exchange/api/", Network.TestNet)
    let! p = b.GetNodesAsync()
    Assert.NotEmpty(p.Nodes)
  }

let pairId = (Bitcoin.Instance :> INetworkSet, Bitcoin.Instance :> INetworkSet)
[<Fact>]
[<Trait("Docker", "Docker")>]
let ``BoltzClient tests (CreateSwap)`` () = task {
    let b = getLocalBoltzClient()
    let! e = Assert.ThrowsAsync<HttpRequestException>(Func<Task>(fun () -> b.GetSwapTransactionAsync("Foo") :> Task))
    Assert.Contains("could not find swap with id", e.Message)

    let lndC = getUserLndClient()

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
    let id = resp.Id
    // ------

    let! statusResp = b.GetSwapStatusAsync(id)
    Assert.Equal(SwapStatusType.InvoiceSet, statusResp.SwapStatus)
  }

[<Fact>]
[<Trait("Docker", "Docker")>]
let ``BoltzClient tests (CreateReverseSwap)`` () = task {
    let b = getLocalBoltzClient()
    let lndC = getUserLndClient()

    let preImage = RandomUtils.GetBytes(32)
    let preImageHash = preImage |> Hashes.SHA256 |> uint256
    let claimKey = new Key()
    let! resp =
      b.CreateReverseSwapAsync({ CreateReverseSwapRequest.OrderSide = OrderType.buy
                                 PairId = pairId
                                 ClaimPublicKey = claimKey.PubKey
                                 InvoiceAmount = Money.Satoshis 100000m
                                 PreimageHash = preImageHash })
    Assert.NotNull(resp)
    Assert.NotNull(resp.Invoice)
    Assert.NotNull(resp.LockupAddress)
    Assert.True(resp.OnchainAmount.Satoshi > 0L)
    Assert.True(resp.TimeoutBlockHeight.Value > 0u)

    let! statusResp = b.GetSwapStatusAsync(resp.Id)
    Assert.Equal(SwapStatusType.Created, statusResp.SwapStatus)
  }
