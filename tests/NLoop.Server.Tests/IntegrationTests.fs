namespace NLoop.Server.Tests

open System.Threading
open BoltzClient
open DotNetLightning.Utils
open LndClient
open NBitcoin.Altcoins
open NLoop.Domain
open NLoop.Server.Tests
open Xunit
open NBitcoin
open FSharp.Control.Tasks
open FSharp.Control

type IntegrationTests() =

  [<Fact>]
  [<Trait("Docker", "On")>]
  member this.TestSubscribeSingleInvoice() =
    task {
      let cli = ExternalClients.GetExternalServiceClients()
      use cts = new CancellationTokenSource()
      cts.CancelAfter(30000)
      let req = {
        ChannelBalanceRequirement.MinimumOutgoing = LNMoney.Zero
        ChannelBalanceRequirement.MinimumIncoming = LNMoney.Satoshis(200001L)
      }
      let! _ = cli.AssureChannelIsOpen(req, cts.Token)

      let amount = 100000L |> LNMoney.Satoshis
      let! inv = cli.User.BitcoinLnd.GetInvoice(amount, preimage)
      let! resp =
        let req = {
          CreateSwapRequest.Invoice = inv
          PairId = PairId(SupportedCryptoCode.BTC, SupportedCryptoCode.LTC)
          OrderSide = OrderType.buy
          RefundPublicKey = refundKey.PubKey
        }
        cli.Server.Boltz.CreateSwapAsync(req, cts.Token)

      let invoiceSubscription =
        cli.User.BitcoinLnd.SubscribeSingleInvoice(inv.PaymentHash, cts.Token)

      let feeRate =
        NLoop.Server.Constants.FallbackFeeSatsPerByte |> decimal |> FeeRate
      let changeAddress = pubkey1.WitHash
      let! unspents =
        cli.Bitcoin.ListUnspentAsync()
      let utxos = unspents |> Array.map(fun uc -> uc.AsCoin() :> ICoin)
      let psbt =
        Transactions.createSwapPSBT
          utxos
          resp.RedeemScript
          resp.ExpectedAmount
          feeRate
          changeAddress
          Litecoin.Instance.Regtest
        |> function | Ok psbt -> psbt | Error e -> failwith e
      let! psbtResp = cli.Bitcoin.WalletProcessPSBTAsync(psbt, true)
      Assert.True(psbtResp.Complete)
      let! _ = cli.Bitcoin.SendRawTransactionAsync(psbtResp.PSBT.Finalize().ExtractTransaction())
      let! _ = cli.Bitcoin.GenerateAsync(1)
      let! isInvoiceSettled =
        asyncSeq {
          for state in invoiceSubscription do
            match state.InvoiceState with
            | IncomingInvoiceStateUnion.Settled ->
              return ()
            | state ->
                printfn $"state: {state}"
                let! _ = cli.Bitcoin.GenerateAsync(1) |> Async.AwaitTask
                ()
        }
        |> AsyncSeq.tryFirst
      Assertion.isSome(isInvoiceSettled)
    }

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
    Assertion.isSome boltzResult.Value.Transaction
    Assert.NotNull boltzResult.Value.Transaction.Value.Tx

    let paymentSeq =
      cli.User.BitcoinLnd.TrackPayment(resp.Invoice.PaymentHash, Some cts.Token)
    let! paymentState = paymentSeq |> AsyncSeq.tryFirst
    Assertion.isSome paymentState
    Assert.Equal (OutgoingInvoiceStateUnion.InFlight, paymentState.Value.InvoiceState)

    ()
  }
