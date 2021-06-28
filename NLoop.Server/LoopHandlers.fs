namespace NLoop.Server

open System
open System.Linq
open System.Threading.Tasks
open FSharp.Control.Reactive

open BTCPayServer.Lightning
open FsToolkit.ErrorHandling
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
        let preimageHash = preimage.Hash

        match req.Validate(opts.Value) with
        | Error errors ->
          ctx.SetStatusCode StatusCodes.Status400BadRequest
          return! json {| errors = errors.ToArray() |} next ctx
        | Ok _ ->
        let counterPartyPair =
          req.CounterPartyPair
          |> Option.defaultValue<SupportedCryptoCode> (ourCryptoCode)

        let! outResponse =
          let req =
            { CreateReverseSwapRequest.InvoiceAmount = req.Amount
              PairId = (ourCryptoCode, counterPartyPair)
              OrderSide = OrderType.buy
              ClaimPublicKey = claimKey.PubKey
              PreimageHash = preimageHash.Value }
          boltzCli.CreateReverseSwapAsync(req)

        let lnClient = ctx.GetService<ILightningClientProvider>().GetClient(ourCryptoCode)
        let! addr =
          match req.Address with
          | Some addr -> Task.FromResult addr
          | None ->
            lnClient.GetDepositAddress()

        let loopOut = {
          LoopOut.Id = outResponse.Id |> SwapId
          Status = SwapStatusType.SwapCreated
          AcceptZeroConf = req.AcceptZeroConf
          ClaimKey = claimKey
          Preimage = preimage
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
        match outResponse.Validate(preimageHash.Value, claimKey.PubKey , req.Amount, opts.Value.MaxAcceptableSwapFee, n) with
        | Error e ->
          do! actor.Execute(loopOut.Id, Swap.Msg.SetValidationError(e), "handleLoopOut")
          return! (error503 e) next ctx
        | Ok () ->
          if (not req.AcceptZeroConf) then
            do! actor.Execute(loopOut.Id, Swap.Msg.NewLoopOut(loopOut))
            ctx.GetService<ISwapEventListener>().RegisterSwap(outResponse.Id, n)
            let response = {
              LoopOutResponse.Id = outResponse.Id
              Address = outResponse.LockupAddress
              ClaimTxId = None
            }
            return! json response next ctx
          else
            let obs =
              ctx
                .GetService<IEventAggregator>()
                .GetObservable<Swap.Event, Swap.Error>()
            do! actor.Execute(loopOut.Id, Swap.Msg.NewLoopOut(loopOut))
            ctx.GetService<ISwapEventListener>().RegisterSwap(outResponse.Id, n)
            let! first =
              obs.FirstAsync().GetAwaiter() |> Async.AwaitCSharpAwaitable |> Async.StartAsTask
            let! second =
              obs.FirstAsync().GetAwaiter() |> Async.AwaitCSharpAwaitable |> Async.StartAsTask
            match (first, second) with
            | Choice1Of2 _ev, Choice1Of2(Swap.Event.ClaimTxPublished(txId)) ->
              let response = {
                LoopOutResponse.Id = outResponse.Id
                Address =outResponse.LockupAddress
                ClaimTxId = txId |> Some
              }
              return! json response next ctx
            | Choice2Of2 e, _
            | _, Choice2Of2 e ->
              return! (error503 e) next ctx
            | a, b ->
              return failwithf "Unreachable! (%A, %A)" a b
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
        let id = inResponse.Id |> SwapId
        match inResponse.Validate(invoice.PaymentHash.Value, refundKey.PubKey, loopIn.Amount, opts.Value.MaxAcceptableSwapFee, n) with
        | Error e ->
          do! actor.Execute(id, Swap.Msg.SetValidationError(e))
          return! (error503 e) next ctx
        | Ok _events ->
          let loopIn = {
            LoopIn.Id = id
            Status = SwapStatusType.InvoiceSet
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
          do! actor.Execute(id, Swap.Msg.NewLoopIn(loopIn))
          let response = {
            LoopInResponse.Id = inResponse.Id
            Address = inResponse.Address
          }
          return! json response next ctx
      }
