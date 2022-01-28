namespace NLoop.Server.Tests

open BoltzClient
open DotNetLightning.Utils
open FSharp.Control
open NBitcoin
open Xunit
open FSharp.Control.Tasks
open LndClient

type BoltzClientTests() =

  [<Fact>]
  [<Trait("Docker", "On")>]
  member this.TestListenSwaps() = task {
    let cli = Helpers.getLocalBoltzClient()

    let! resp =
      let req = {
        CreateReverseSwapRequest.PairId = pairId
        OrderSide = OrderType.buy
        ClaimPublicKey = claimKey.PubKey
        InvoiceAmount = Money.Satoshis(100000L)
        PreimageHash = preimage.Hash.Value
      }
      cli.CreateReverseSwapAsync(req)
    let! s =
      asyncSeq {
        for s in cli.StartListenToSwapStatusChange(resp.Id) do
          ()
      } |> AsyncSeq.toArrayAsync

    let lnClient = Helpers.userLndClient
    let! _ =
      let req = {
        SendPaymentRequest.Invoice = resp.Invoice
        MaxFee = 10L |> Money.Satoshis
        OutgoingChannelIds = [||]
        TimeoutSeconds = 2
      }
      lnClient.Offer(req)
    ()
  }
