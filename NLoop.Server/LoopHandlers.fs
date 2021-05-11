namespace NLoop.Server

open System
open System.Linq
open System.Threading.Tasks
open FSharp.Control.Reactive

open BTCPayServer.Lightning
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.Crypto
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.DTOs
open NLoop.Server.Services
open System.Reactive.Linq

module LoopHandlers =
  open Microsoft.AspNetCore.Http
  open FSharp.Control.Tasks
  open Giraffe

  let handleLoopOut (ourCryptoCode: SupportedCryptoCode) (req: LoopOutRequest) =
    fun (next : HttpFunc) (ctx : HttpContext) ->
      task {
        let repo = ctx.GetService<IRepositoryProvider>().GetRepository ourCryptoCode
        let opts = ctx.GetService<IOptions<NLoopOptions>>()
        let n = opts.Value.GetNetwork(ourCryptoCode)
        let boltzCli = ctx.GetService<BoltzClient>()

        use! claimKey = repo.NewPrivateKey()
        let! preimage = repo.NewPreimage()
        let preimageHash = preimage |> Hashes.SHA256

        let counterPartyPair =
          req.CounterPartyPair
          |> Option.defaultValue<SupportedCryptoCode> (ourCryptoCode)
        let! outResponse =
          let req =
            { CreateReverseSwapRequest.InvoiceAmount = req.Amount
              PairId = (ourCryptoCode, counterPartyPair)
              OrderSide = OrderType.buy
              ClaimPublicKey = claimKey.PubKey
              PreimageHash = preimageHash |> uint256 }
          boltzCli.CreateReverseSwapAsync(req)

        ctx.GetService<ISwapEventListener>().RegisterSwap(outResponse.Id, n)

        let lnClient = ctx.GetService<ILightningClientProvider>().GetClient(ourCryptoCode)
        let! addr =
          match req.Address with
          | Some addr -> Task.FromResult addr
          | None ->
            lnClient.GetDepositAddress()

        let loopOut = {
          LoopOut.Id = outResponse.Id
          Status = SwapStatusType.SwapCreated
          Error = String.Empty
          AcceptZeroConf = req.AcceptZeroConf
          PrivateKey = claimKey
          Preimage = preimage |> uint256
          RedeemScript = outResponse.RedeemScript
          Invoice = outResponse.Invoice.ToString()
          ClaimAddress = addr.ToString()
          OnChainAmount = outResponse.OnchainAmount
          TimeoutBlockHeight = outResponse.TimeoutBlockHeight
          LockupTransactionId = None
          ClaimTransactionId = None
          PairId = ourCryptoCode, counterPartyPair
        }

        let actor = ctx.GetService<SwapActor>()
        match outResponse.Validate(uint256(preimageHash), claimKey.PubKey , req.Amount, opts.Value.MaxAcceptableSwapFee, n) with
        | Error e ->
          do! actor.Put(Swap.Msg.SetValidationError(loopOut.Id, e))
          ctx.SetStatusCode StatusCodes.Status503ServiceUnavailable
          return! ctx.WriteJsonAsync({| error = e |})
        | Ok () ->
          if (not req.AcceptZeroConf) then
            do! actor.Put(Swap.Msg.NewLoopOut(loopOut))
            let response = {
              LoopOutResponse.Id = outResponse.Id
              Address = BitcoinAddress.Create(outResponse.LockupAddress, n)
              ClaimTxId = None
            }
            return! json response next ctx
          else
            let obs =
              let eventAggregator = ctx.GetService<IEventAggregator>()
              eventAggregator.GetObservable<Swap.Event, Swap.Error>()
            do! actor.Put(Swap.Msg.NewLoopOut(loopOut))
            let! first =
              obs.FirstAsync().GetAwaiter() |> Async.AwaitCSharpAwaitable |> Async.StartAsTask
            let! second =
              obs.FirstAsync().GetAwaiter() |> Async.AwaitCSharpAwaitable |> Async.StartAsTask
            match (first, second) with
            | Choice1Of2 _ev, Choice1Of2 ev2 ->
              let txId =
                match ev2 with
                | Swap.Event.ClaimTxPublished(txid, _) -> Some txid
                | _ -> None
              let response = {
                LoopOutResponse.Id = outResponse.Id
                Address = BitcoinAddress.Create(outResponse.LockupAddress, n)
                ClaimTxId = txId
              }
              return! json response next ctx
            | Choice2Of2 e, _  ->
              ctx.SetStatusCode StatusCodes.Status503ServiceUnavailable
              return! ctx.WriteJsonAsync({| error = e |})
            | _, Choice2Of2 e ->
              ctx.SetStatusCode StatusCodes.Status503ServiceUnavailable
              return! ctx.WriteJsonAsync({| error = e |})
      }

  let handleLoopIn (ourCryptoCode: SupportedCryptoCode) (loopIn: LoopInRequest) =
    fun (next : HttpFunc) (ctx : HttpContext) ->
      task {
        let repo = ctx.GetService<IRepositoryProvider>().GetRepository ourCryptoCode
        let opts = ctx.GetService<IOptions<NLoopOptions>>()
        let n = opts.Value.GetNetwork(ourCryptoCode)
        let boltzCli = ctx.GetService<BoltzClient>()

        let! refundKey = repo.NewPrivateKey()
        let! invoice =
          let amt = LightMoney.Satoshis(loopIn.Amount.Satoshi)
          ctx
            .GetService<ILightningClientProvider>()
            .GetClient(ourCryptoCode)
            .CreateInvoice(amt, $"This is an invoice for LoopIn by NLoop ({loopIn.Label})", TimeSpan.FromMinutes(5.))
        let invoice = invoice.ToDNLInvoice()
        let counterPartyPair =
          loopIn.CounterPartyPair
          |> Option.defaultValue<SupportedCryptoCode> (ourCryptoCode)
        let! inResponse =
          let req =
            { CreateSwapRequest.Invoice = invoice
              PairId = (ourCryptoCode, counterPartyPair)
              OrderSide = OrderType.buy
              RefundPublicKey = refundKey.PubKey }
          boltzCli.CreateSwapAsync(req)

        ctx.GetService<ISwapEventListener>().RegisterSwap(inResponse.Id, n)

        let actor = ctx.GetService<SwapActor>()
        match inResponse.Validate(invoice.PaymentHash.Value, refundKey.PubKey, loopIn.Amount, opts.Value.MaxAcceptableSwapFee, n) with
        | Error e ->
          do! actor.Put(Swap.Msg.SetValidationError(inResponse.Id, e))
          ctx.SetStatusCode StatusCodes.Status503ServiceUnavailable
          return! ctx.WriteJsonAsync({| error = e |})
        | Ok () ->
        let loopIn = {
          LoopIn.Id = inResponse.Id
          Status = SwapStatusType.InvoiceSet
          Error = String.Empty
          PrivateKey = refundKey
          Preimage = None
          RedeemScript = inResponse.RedeemScript
          Invoice = invoice.ToString()
          Address = inResponse.Address.ToString()
          ExpectedAmount = Money.Zero
          TimeoutBlockHeight = inResponse.TimeoutBlockHeight
          LockupTransactionId = None
          RefundTransactionId = None
          PairId = (ourCryptoCode, counterPartyPair) }
        do! actor.Put(Swap.Msg.NewLoopIn(loopIn))
        let response = {
          LoopInResponse.Id = inResponse.Id
          Address = BitcoinAddress.Create(inResponse.Address, n)
        }
        return! json response next ctx
      }
