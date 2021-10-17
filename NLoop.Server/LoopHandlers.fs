module NLoop.Server.LoopHandlers

open System
open System.Linq
open System.Threading.Tasks
open DotNetLightning.Utils.Primitives
open FSharp.Control.Reactive

open FsToolkit.ErrorHandling
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.Crypto
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.DTOs
open NLoop.Server.Services
open System.Reactive.Linq

open DotNetLightning.Utils

open Microsoft.AspNetCore.Http
open FSharp.Control.Tasks
open Giraffe


let handleLoopOutCore (req: LoopOutRequest) =
  fun (next : HttpFunc) (ctx : HttpContext) ->
    task {
      let opts = ctx.GetService<IOptions<NLoopOptions>>()
      let boltzCli = ctx.GetService<BoltzClient>()
      let struct(baseCryptoCode, quoteCryptoCode) as pairId =
        req.PairId
        |> Option.defaultValue PairId.Default
      let height = ctx.GetBlockHeight(baseCryptoCode)
      let actor = ctx.GetService<SwapActor>()
      let f = boltzCli.CreateReverseSwapAsync
      let obs =
        ctx
          .GetService<IEventAggregator>()
          .GetObservable<Swap.EventWithId, Swap.ErrorWithId>()
          .Replay()

      match! actor.ExecNewLoopOut(f, req, height) with
      | Error e ->
        return! (error503 e) next ctx
      | Ok loopOut ->
      if (not req.AcceptZeroConf) then
        let response = {
          LoopOutResponse.Id = loopOut.Id.Value
          Address = loopOut.ClaimAddress
          ClaimTxId = None
        }
        return! json response next ctx
      else
        let firstErrorOrTxIdT =
          obs
          |> Observable.filter(function
                               | Choice1Of2 { Id = swapId }
                               | Choice2Of2 { Id = swapId } -> swapId = loopOut.Id)
          |> Observable.choose(
            function
            | Choice1Of2({ Event = Swap.Event.ClaimTxPublished txId }) -> txId |> Ok |> Some
            | Choice1Of2( { Event = Swap.Event.FinishedByError(_id, err) }) -> err |> Error |> Some
            | Choice2Of2({ Error = e }) -> e.ToString() |> Error |> Some
            | _ -> None
            )
          |> fun o -> o.FirstAsync().GetAwaiter() |> Async.AwaitCSharpAwaitable |> Async.StartAsTask
        use _ = obs.Connect()
        match! firstErrorOrTxIdT with
        | Error e ->
          return! (error503 e) next ctx
        | Ok txid ->
          let response = {
            LoopOutResponse.Id = loopOut.Id.Value
            Address = loopOut.ClaimAddress
            ClaimTxId = Some txid
          }
          return! json response next ctx
    }

let handleLoopOut (req: LoopOutRequest) =
  fun (next : HttpFunc) (ctx : HttpContext) ->
    task {
      let opts = ctx.GetService<IOptions<NLoopOptions>>()
      let pairId =
        req.PairId
        |> Option.defaultValue PairId.Default
      let struct(_baseAsset, quoteAsset) =
        pairId
      return!
        (checkBlockchainIsSyncedAndSetTipHeight pairId
         >=> checkWeHaveRouteToCounterParty quoteAsset req.Amount
         >=> handleLoopOutCore req)
          next ctx
    }
let handleLoopInCore (loopIn: LoopInRequest) =
  fun (next : HttpFunc) (ctx : HttpContext) ->
    task {
      let pairId =
        loopIn.PairId
        |> Option.defaultValue PairId.Default
      let struct(baseCryptoCode, quoteCryptoCode) =
        pairId
      let opts = ctx.GetService<IOptions<NLoopOptions>>()
      let onChainNetwork = opts.Value.GetNetwork(quoteCryptoCode)
      let boltzCli = ctx.GetService<BoltzClient>()

      let refundKey = new Key()
      let preimage = RandomUtils.GetBytes 32 |> PaymentPreimage.Create

      let! invoice =
        let amt = loopIn.Amount.ToLNMoney()
        ctx
          .GetService<ILightningClientProvider>()
          .GetClient(baseCryptoCode)
          .GetInvoice(preimage, amt, TimeSpan.FromMinutes(float(10 * 6)), $"This is an invoice for LoopIn by NLoop (label: \"{loopIn.Label}\")")

      let! inResponse =
        let req =
          { CreateSwapRequest.Invoice = invoice
            PairId = pairId
            OrderSide = OrderType.buy
            RefundPublicKey = refundKey.PubKey }
        boltzCli.CreateSwapAsync(req)

      let actor = ctx.GetService<SwapActor>()
      let id = inResponse.Id |> SwapId
      match inResponse.Validate(invoice.PaymentHash.Value,
                                refundKey.PubKey,
                                loopIn.Amount,
                                loopIn.MaxSwapFee |> ValueOption.defaultToVeryHighFee,
                                onChainNetwork) with
      | Error e ->
        do! actor.Execute(id, Swap.Command.MarkAsErrored(e))
        return! (error503 e) next ctx
      | Ok _events ->
        let loopIn = {
          LoopIn.Id = id
          Status = SwapStatusType.InvoiceSet
          RefundPrivateKey = refundKey
          Preimage = None
          RedeemScript = inResponse.RedeemScript
          Invoice = invoice.ToString()
          Address = inResponse.Address.ToString()
          ExpectedAmount = inResponse.ExpectedAmount
          TimeoutBlockHeight = inResponse.TimeoutBlockHeight
          LockupTransactionHex = None
          RefundTransactionId = None
          PairId = pairId
          ChainName = opts.Value.ChainName.ToString()
          Label = loopIn.Label |> Option.defaultValue String.Empty
          HTLCConfTarget =
            loopIn.HtlcConfTarget
            |> ValueOption.defaultValue Constants.DefaultHtlcConfTarget
            |> uint |> BlockHeightOffset32
          Cost = SwapCost.Zero
        }
        let height = ctx.GetBlockHeight(quoteCryptoCode)
        do! actor.Execute(id, Swap.Command.NewLoopIn(height, loopIn))
        let response = {
          LoopInResponse.Id = inResponse.Id
          Address = inResponse.Address
        }
        return! json response next ctx
    }
let handleLoopIn (loopIn: LoopInRequest) =
  fun (next : HttpFunc) (ctx : HttpContext) ->
    task {
      let handle = (handleLoopInCore loopIn)
      let pairId =
        loopIn.PairId
        |> Option.defaultValue (PairId.Default)
      return!
        (checkBlockchainIsSyncedAndSetTipHeight pairId >=>
         handle)
          next ctx
    }

