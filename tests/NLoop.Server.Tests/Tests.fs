module Tests

open DotNetLightning.Utils
open Helpers
open System
open System.Net.Http
open System.Threading.Tasks
open BTCPayServer.Lightning
open NBitcoin
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

[<Fact>]
[<Trait("Docker", "Docker")>]
let ``BoltzClient tests (GetSwapStatus)`` () = task {
    let b = getLocalBoltzClient()
    let! e = Assert.ThrowsAsync<HttpRequestException>(Func<Task>(fun () -> b.GetSwapTransactionAsync("Foo") :> Task))
    Assert.Contains("could not find swap with id", e.Message)

    // create channel
    let lndC = getUserLndClient()

    // --- create swap ---
    let refundKey = new Key()
    let! invoice =
      lndC.CreateInvoice(amount=(LNMoney.Satoshis 100000).ToLightMoney(), description="test", expiry=TimeSpan.FromMinutes(5.))
    let! resp =
      b.CreateSwapAsync({ PairId = (Bitcoin.Instance :> INetworkSet, Bitcoin.Instance :> INetworkSet)
                          OrderSide = OrderType.Buy
                          RefundPublicKey = refundKey.PubKey
                          Invoice = invoice.ToDNLInvoice() })

    Assert.NotNull(resp)
    Assert.NotNull(resp.Address)
    Assert.NotNull(resp.ClaimAddress)
    Assert.NotNull(resp.ExpectedAmount)
    Assert.NotNull(resp.TimeoutBlockHeight)
    let id = resp.Id
    // ------

    let! statusResp = b.GetSwapStatusAsync(id)
    Assert.Equal(SwapStatusType.Created, statusResp.SwapStatus)
  }

