namespace NLoop.Server.Tests

open System.Threading
open System.Threading.Tasks
open BoltzClient
open DotNetLightning.Utils
open LndClient
open Microsoft.Extensions.DependencyInjection
open NBitcoin.Altcoins
open NBitcoin.RPC
open NLoop.Domain
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.DTOs
open NLoop.Server.Tests
open Xunit
open NBitcoin
open FSharp.Control.Tasks
open FSharp.Control
open FSharp.Control.Reactive

type IntegrationTests() =

  [<Fact>]
  [<Trait("Docker", "On")>]
  member this.TestSubscribeSingleInvoice() =
    task {
      let cli = ExternalClients.GetExternalServiceClients()
      use cts = new CancellationTokenSource()
      cts.CancelAfter(10000)
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
        cli.Litecoin.ListUnspentAsync()
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
      let! psbtResp = cli.Litecoin.WalletProcessPSBTAsync(psbt, true)
      Assert.True(psbtResp.Complete)
      let! _ = cli.Litecoin.SendRawTransactionAsync(psbtResp.PSBT.Finalize().ExtractTransaction())
      let! _ = cli.Litecoin.GenerateAsync(1)
      let! litecoinAddr =
        let req = GetNewAddressRequest()
        req.AddressType <- AddressType.Legacy
        cli.Litecoin.GetNewAddressAsync(req)
      let! isInvoiceSettled =
        asyncSeq {
          for state in invoiceSubscription do
            match state.InvoiceState with
            | IncomingInvoiceStateUnion.Settled ->
              yield true
            | _state ->
                let! _ = cli.Litecoin.GenerateToAddressAsync(3, litecoinAddr) |> Async.AwaitTask
                yield false
        }
        |> AsyncSeq.toArrayAsync
        |> fun a -> Async.StartAsTask(a, TaskCreationOptions.None, cts.Token)

      Assert.Contains(isInvoiceSettled, id)
    }

  [<Fact>]
  [<Trait("Docker", "On")>]
  member this.TestListenSwaps() = task {
    let cli = ExternalClients.GetExternalServiceClients()
    use cts = new CancellationTokenSource()
    cts.CancelAfter(10000)
    let! _ = cli.AssureChannelIsOpen(LNMoney.Satoshis(500000L), cts.Token)

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
  [<Fact>]
  [<Trait("Docker", "On")>]
  member this.TestWalletKit() =
    task {
      let cli = ExternalClients.GetExternalServiceClients()
      use cts = new CancellationTokenSource()
      cts.CancelAfter(5000)
      do! cli.AssureWalletIsReady(cts.Token)
      let dest = pubkey1.WitHash.GetAddress(Network.RegTest)
      let amt = Money.Satoshis(100000L)
      let! txid = cli.User.BitcoinLnd.SendCoins(dest, amt, BlockHeightOffset32(6u), cts.Token)
      let _tx = cli.Bitcoin.GetRawTransactionAsync(txid)
      ()
    }

  [<Fact>]
  [<Trait("Docker", "On")>]
  member this.LoopInIntegrationTest() =
    task {
      use cli = Clients.Create()
      use cts = new CancellationTokenSource()
      cts.CancelAfter(10000)
      let! _ =
        let req = {
          ChannelBalanceRequirement.MinimumIncoming = 1000000L |> LNMoney.Satoshis
          MinimumOutgoing = LNMoney.Zero
        }
        cli.ExternalClients.AssureChannelIsOpen(req, cts.Token)

      let eventAggregator = cli.NLoopServer.Services.GetRequiredService<IEventAggregator>()
      let obs = eventAggregator.GetObservable<Swap.EventWithId, Swap.ErrorWithId>() |> Observable.replay
      use _ = obs |> Observable.connect
      let amount = 100000L
      let! _resp =
        let req = NLoopClient.LoopInRequest()
        req.Amount <- amount
        req.Pair_id <- "BTC/LTC"
        req.Max_swap_fee <- 60000L
        cli.NLoopClient.InAsync(req, cts.Token)

      let! Ok txHex =
        obs |> Observable.chooseOrError(function | Swap.Event.OurSwapTxPublished p -> Some p.TxHex | _ -> None)
      let tx = Transaction.Parse(txHex, Litecoin.Instance.Regtest)

      let! rawTx = cli.ExternalClients.Litecoin.GetRawTransactionAsync(tx.GetHash())
      Assert.Equal(txHex, rawTx.ToHex())

      let! ltcAddr = cli.ExternalClients.Litecoin.GetNewAddressAsync()
      let! _ = cli.ExternalClients.Litecoin.GenerateToAddressAsync(3, ltcAddr)

      let! Ok txId =
        obs |> Observable.chooseOrError(function | Swap.Event.OurSwapTxConfirmed p -> Some p.TxId | _ -> None)
      Assert.Equal(tx.GetHash(), txId)
      let! Ok actualAmount =
        obs
        |> Observable.chooseOrError(function | Swap.Event.OffChainPaymentReceived p -> Some p.Amount | _ -> None)
      Assert.Equal(actualAmount.Satoshi, amount)
      let! _ = cli.ExternalClients.Litecoin.GenerateToAddressAsync(3, ltcAddr)
      let! Ok () =
        obs |> Observable.chooseOrError(function | Swap.Event.SuccessTxConfirmed _ -> Some () | _ -> None)
      let! Ok () =
        obs |> Observable.chooseOrError(function | Swap.Event.FinishedSuccessfully _ -> Some () | _ -> None)
      ()
    }
  [<Fact>]
  [<Trait("Docker", "On")>]
  member this.LoopOutIntegrationTest() =
    task {
      use cli = Clients.Create()
      use cts = new CancellationTokenSource()
      cts.CancelAfter(10000)
      let! _ =
        cli.ExternalClients.AssureChannelIsOpen(1000000L |> LNMoney.Satoshis, cts.Token)

      let eventAggregator = cli.NLoopServer.Services.GetRequiredService<IEventAggregator>()
      let obs = eventAggregator.GetObservable<Swap.EventWithId, Swap.ErrorWithId>() |> Observable.replay
      use _ = obs |> Observable.connect
      let amount = 100000L
      let! _resp =
        let req = NLoopClient.LoopOutRequest()
        req.Amount <- amount
        req.Pair_id <- "LTC/BTC"
        req.Max_swap_fee <- 60000L
        req.Swap_tx_conf_requirement <- 1
        cli.NLoopClient.OutAsync(req, cts.Token)

      let! Ok _offerStartedData =
        obs |> Observable.chooseOrError(function | Swap.Event.OffChainOfferStarted o -> Some o | _ -> None)

      let! _theirTxInfo =
        obs |> Observable.chooseOrError(function | Swap.Event.TheirSwapTxPublished o -> Some o | _ -> None)

      let! ltcAddr = cli.ExternalClients.Litecoin.GetNewAddressAsync()
      let! _ = cli.ExternalClients.Litecoin.GenerateToAddressAsync(1, ltcAddr)
      let! _start =
        obs |> Observable.chooseOrError(function | Swap.Event.TheirSwapTxConfirmedFirstTime o -> Some o | _ -> None)
      let! _ = cli.ExternalClients.Litecoin.GenerateToAddressAsync(1, ltcAddr)
      let! _start =
        obs |> Observable.chooseOrError(function | Swap.Event.ClaimTxPublished o -> Some o | _ -> None)
      let! _offerResolved =
        obs |> Observable.chooseOrError(function | Swap.Event.OffchainOfferResolved o -> Some o | _ -> None)
      let! _ = cli.ExternalClients.Litecoin.GenerateToAddressAsync(1, ltcAddr)
      let! _start =
        obs |> Observable.chooseOrError(function | Swap.Event.ClaimTxConfirmed o -> Some o | _ -> None)
      let! _start =
        obs |> Observable.chooseOrError(function | Swap.Event.FinishedSuccessfully o -> Some o | _ -> None)
      ()
    }
