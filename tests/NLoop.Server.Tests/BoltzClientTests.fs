namespace NLoop.Server.Tests

open System.Threading
open BoltzClient
open DotNetLightning.Utils
open FSharp.Control
open NBitcoin
open NLoop.Server.Tests.Extensions
open Xunit
open FSharp.Control.Tasks
open LndClient

type BoltzClientTests() =

  [<Fact>]
  [<Trait("Docker", "On")>]
  member this.TestListenSwaps() = task {
    let cli = ExternalClients.GetExternalServiceClients()
    use cts = new CancellationTokenSource()
    cts.CancelAfter(30000)
    let! _ = cli.AssureChannelIsOpen(LNMoney.Satoshis(5000000L), cts.Token)

    let! resp =
      let req = {
        CreateReverseSwapRequest.PairId = pairId
        OrderSide = OrderType.buy
        ClaimPublicKey = claimKey.PubKey
        InvoiceAmount = Money.Satoshis(100000L)
        PreimageHash = preimage.Hash.Value
      }
      cli.Server.Boltz.CreateReverseSwapAsync(req, cts.Token)
    let listenTask =
        cli.Server.Boltz.StartListenToSwapStatusChange(resp.Id, cts.Token)
        |> AsyncSeq.tryFirst
    let! _ =
      let req = {
        SendPaymentRequest.Invoice = resp.Invoice
        MaxFee = 10L |> Money.Satoshis
        OutgoingChannelIds = [||]
        TimeoutSeconds = 2
      }
      cli.User.BitcoinLnd.Offer(req, cts.Token)
    let! boltzResult = listenTask
    Assertion.isSome boltzResult
    ()
  }
