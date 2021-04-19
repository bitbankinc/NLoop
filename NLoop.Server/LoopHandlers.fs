namespace NLoop.Server

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.Altcoins
open NBitcoin.Crypto
open NLoop.Domain
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.DTOs
open NLoop.Server.Services
module private HandlerHelpers =
  open Giraffe
  open System.Threading.Tasks
  let earlyReturn : HttpFunc = Some >> Task.FromResult

module LoopHandlers =
  open Microsoft.AspNetCore.Http
  open FSharp.Control.Tasks
  open Giraffe

  let handleLoopOut (cryptoCode: string) (req: LoopOutRequest) =
    fun (next : HttpFunc) (ctx : HttpContext) ->
      task {
        match SupportedCryptoCode.Parse cryptoCode with
        | Error e ->
          ctx.SetStatusCode 400
          return! ctx.WriteJsonAsync({| error = e |})
        | Ok ourCryptoCode ->
        let repo = ctx.GetService<IRepositoryProvider>().GetRepository cryptoCode
        let opts = ctx.GetService<IOptions<NLoopOptions>>()
        let n = opts.Value.GetNetwork(ourCryptoCode)
        let boltzCli = ctx.GetService<BoltzClientProvider>().Invoke(n)

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

        let! addr =
          match req.Address with
          | Some addr -> Task.FromResult addr
          | None ->
            ctx.GetService<LightningClientProvider>().GetClient(ourCryptoCode).GetDepositAddress()

        let reverseSwap = {
          LoopOut.Id = outResponse.Id
          Status = SwapStatusType.Created
          Error = String.Empty
          AcceptZeroConf = req.AcceptZeroConf
          PrivateKey = claimKey
          Preimage = preimage |> uint256
          RedeemScript = outResponse.RedeemScript
          Invoice = outResponse.Invoice.ToString() // failwith "todo"
          ClaimAddress = addr.ToString()
          OnChainAmount = outResponse.OnchainAmount
          TimeoutBlockHeight = outResponse.TimeoutBlockHeight
          LockupTransactionId = None
          ClaimTransactionId = None
          PairId = ourCryptoCode, counterPartyPair
        }

        match outResponse.Validate(uint256 preimageHash, req.Amount, opts.Value.MaxAcceptableSwapFee) with
        | Error e ->
          do! repo.SetLoopOut({ reverseSwap with Error = e })
          ctx.SetStatusCode StatusCodes.Status503ServiceUnavailable
          return! ctx.WriteJsonAsync({| error = e |})
        | Ok _ ->
          let actor = ctx.GetService<SwapActor>()
          do! actor.Put(Swap.Command.NewLoopOut(reverseSwap))
          let eventAggregator = ctx.GetService<EventAggregator>()
          let mutable txId = None
          if (req.AcceptZeroConf) then
            let! e = eventAggregator.WaitNext<Swap.Event>(function Swap.Event.ClaimTxPublished(_txid, swapId) -> swapId = reverseSwap.Id | _ -> false)
            txId <-
              e
              |> function Swap.Event.ClaimTxPublished (txid, _swapId) -> txid
              |> Some
          let response = {
            LoopOutResponse.Id = outResponse.Id
            Address = outResponse.LockupAddress
            ClaimTxId = txId
          }
          return! json response next ctx
      }

  let handleLoopIn (cryptoCode: string) (loopIn: LoopInRequest) =
    fun (next : HttpFunc) (ctx : HttpContext) ->
      task {
        let response = {
          LoopInResponse.Id = (ShortGuid.fromGuid(Guid()))
          Address = BitcoinAddress.Create("bc1qcw9l54jre2wc4uju222wz8su6am2fs3vufsc8c", Network.RegTest)
        }
        return! json response next ctx
      }
