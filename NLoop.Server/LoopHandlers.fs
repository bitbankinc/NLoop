namespace NLoop.Server

open System
open NLoop.Infrastructure
open NLoop.Infrastructure.DTOs
open NLoop.Server.Services

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
                  return Some ctx
                | Ok ourNetwork ->
                let! outResponse =
                  let req =
                    let counterPartyPair =
                      req.CounterPartyPair |> Option.defaultValue (ourNetwork)
                    { CreateReverseSwapRequest.InvoiceAmount = req.Amount
                      PairId = (ourNetwork, counterPartyPair)
                      OrderSide = failwith "todo"
                      ClaimPublicKey = failwith "todo"
                      PreimageHash = failwith "todo" }
                  boltzCli.CreateReverseSwapAsync(req)
                let response = {
                  LoopOutResponse.Id = (ShortGuid.fromGuid(Guid()))
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
