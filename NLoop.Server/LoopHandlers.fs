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

module private ValueOption =
  let defaultToVeryHighFee(v: Money voption) =
    v |> ValueOption.defaultValue(Money.Coins(100000m))


let handleLoopOutCore (req: LoopOutRequest) =
  fun (next : HttpFunc) (ctx : HttpContext) ->
    task {
      let opts = ctx.GetService<IOptions<NLoopOptions>>()
      let struct(baseCryptoCode, quoteCryptoCode) as pairId =
        req.PairId
        |> Option.defaultValue (PairId.Default)
      let n = opts.Value.GetNetwork(baseCryptoCode)
      let boltzCli = ctx.GetService<BoltzClient>()
      let claimKey = new Key()
      let preimage = RandomUtils.GetBytes 32 |> PaymentPreimage.Create
      let preimageHash = preimage.Hash

      let! outResponse =
        let req =
          { CreateReverseSwapRequest.InvoiceAmount = req.Amount
            PairId = pairId
            OrderSide = OrderType.buy
            ClaimPublicKey = claimKey.PubKey
            PreimageHash = preimageHash.Value }
        boltzCli.CreateReverseSwapAsync(req)

      let lnClient = ctx.GetService<ILightningClientProvider>().GetClient(quoteCryptoCode)
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
        LockupTransactionHex = None
        ClaimTransactionId = None
        PairId = pairId
        ChainName = opts.Value.ChainName.ToString()
        Label = req.Label |> Option.defaultValue String.Empty
        PrepayInvoice =
          outResponse.MinerFeeInvoice
          |> Option.map(fun s -> s.ToString())
          |> Option.defaultValue String.Empty
        MaxMinerFee =
          req.MaxMinerFee |> ValueOption.defaultToVeryHighFee
        SweepConfTarget =
          req.SweepConfTarget
          |> ValueOption.defaultValue Constants.DefaultSweepConfTarget
          |> uint |> BlockHeightOffset32
      }

      let actor = ctx.GetService<SwapActor>()
      match outResponse.Validate(preimageHash.Value,
                                 claimKey.PubKey,
                                 req.Amount,
                                 req.MaxSwapFee |> ValueOption.defaultToVeryHighFee,
                                 req.MaxPrepayAmount |> ValueOption.defaultToVeryHighFee,
                                 n) with
      | Error e ->
        do! actor.Execute(loopOut.Id, Swap.Command.SetValidationError(e), "handleLoopOut")
        return! (error503 e) next ctx
      | Ok () ->
        let loopOutParams = {
          Swap.LoopOutParams.OutgoingChanId = req.ChannelId
          Swap.LoopOutParams.MaxPrepayFee = req.MaxPrepayRoutingFee |> ValueOption.defaultValue(Money.Coins 100000m)
          Swap.LoopOutParams.MaxPaymentFee = req.MaxSwapFee |> ValueOption.defaultValue(Money.Coins 100000m)
          Swap.LoopOutParams.Height = ctx.GetBlockHeight(baseCryptoCode)
        }
        if (not req.AcceptZeroConf) then
          do! actor.Execute(loopOut.Id, Swap.Command.NewLoopOut(loopOutParams, loopOut))
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
              .GetObservable<Swap.EventWithId, Swap.ErrorWithId>()
              |> Observable.filter(function
                                   | Choice1Of2 { Id = swapId }
                                   | Choice2Of2 { Id = swapId } -> swapId.Value = outResponse.Id)

          do! actor.Execute(loopOut.Id, Swap.Command.NewLoopOut(loopOutParams, loopOut))
          let! firstErrorOrTxId =
            obs
            |> Observable.choose(
              function
              | Choice1Of2({ Event = Swap.Event.ClaimTxPublished txId }) -> txId |> box |> Some
              | Choice1Of2( { Event = Swap.Event.FinishedByError(_id, err) }) -> err |> box |> Some
              | Choice2Of2({ Error = e }) -> e.ToString() |> box |> Some
              | _ -> None
              )
            |> fun o -> o.FirstAsync().GetAwaiter() |> Async.AwaitCSharpAwaitable |> Async.StartAsTask

          match firstErrorOrTxId with
          | :? string as e ->
            return! (error503 e) next ctx
          | :? uint256 as txid ->
            let response = {
              LoopOutResponse.Id = outResponse.Id
              Address = outResponse.LockupAddress
              ClaimTxId = Some txid
            }
            return! json response next ctx
          | x ->
            return failwith $"Unreachable: {x}"
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
        do! actor.Execute(id, Swap.Command.SetValidationError(e))
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

