namespace NLoop.Server

open System
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.Crypto
open NLoop.Infrastructure
open NLoop.Infrastructure.DTOs
open NLoop.Server.Services
module private HandlerHelpers =
  open Giraffe
  open FSharp.Control.Tasks
  open System.Threading.Tasks
  let earlyReturn : HttpFunc = Some >> Task.FromResult

module LoopHandlers =
  open Microsoft.AspNetCore.Http
  open FSharp.Control.Tasks
  open Giraffe

  let handleLoopOut (cryptoCode: string) (req: LoopOutRequest) =
    fun (next : HttpFunc) (ctx : HttpContext) ->
      task {
        let boltzCli = ctx.GetService<BoltzClient>()
        match cryptoCode.GetNetworkFromCryptoCode() with
        | Error e ->
          ctx.SetStatusCode 400
          return! ctx.WriteJsonAsync({| error = e |})
        | Ok ourNetwork ->

        let repo = ctx.GetService<Repository>()
        let claimKey = new Key()
        do! repo.SetPrivateKey(claimKey)
        let preimage = RandomUtils.GetBytes(32)
        do! repo.SetPreimage(preimage)
        let preimageHash = preimage |> Hashes.SHA256

        let! outResponse =
          let req =
            let counterPartyPair =
              req.CounterPartyPair |> Option.defaultValue (ourNetwork)
            { CreateReverseSwapRequest.InvoiceAmount = req.Amount
              PairId = (ourNetwork, counterPartyPair)
              OrderSide = OrderType.buy
              ClaimPublicKey = claimKey.PubKey
              PreimageHash = preimageHash |> uint256 }
          boltzCli.CreateReverseSwapAsync(req)

        let conf = ctx.GetService<IOptions<NLoopServerConfig>>()
        match outResponse.Validate(uint256 preimageHash, req.Amount, conf.Value.MaxAcceptableSwapFee) with
        | Error e ->
          ctx.SetStatusCode StatusCodes.Status400BadRequest
          return! ctx.WriteJsonAsync({| error = e |})
        | Ok () ->
          let response = {
            LoopOutResponse.Id = outResponse.Id
            HtlcTarget = outResponse.LockupAddress :?> BitcoinWitScriptAddress
          }
          return! json response next ctx
      }

  let handleLoopIn (cryptoCode: string) (loopIn: LoopInRequest) =
    fun (next : HttpFunc) (ctx : HttpContext) ->
      task {
        let response = {
          LoopInResponse.Id = (ShortGuid.fromGuid(Guid()))
        }
        return! json response next ctx
      }
