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
      let height = ctx.GetBlockHeight(req.PairIdValue.Base)
      let actor = ctx.GetService<ISwapExecutor>()
      match! actor.ExecNewLoopOut(req, height) with
      | Error e ->
        return! (error503 e) next ctx
      | Ok response ->
        return! json response next ctx
    }

let private validateLoopOutRequest (opts: NLoopOptions) (req: LoopOutRequest) =
  fun (next: HttpFunc) ->
    match req.Validate(opts.GetNetwork) with
    | Ok() ->
      next
    | Error errorMsg ->
      errorBadRequest errorMsg next

let handleLoopOut (req: LoopOutRequest) =
  fun (next : HttpFunc) (ctx : HttpContext) ->
    let opts = ctx.GetService<IOptions<NLoopOptions>>()
    (validateLoopOutRequest opts.Value req
     >=> checkBlockchainIsSyncedAndSetTipHeight req.PairIdValue
     >=> checkWeHaveRouteToCounterParty req.PairIdValue.Quote req.Amount req.OutgoingChannelIds
     >=> validateFeeLimitAgainstServerQuote req
     >=> handleLoopOutCore req)
      next ctx

let handleLoopInCore (loopIn: LoopInRequest) =
  fun (next : HttpFunc) (ctx : HttpContext) ->
    let actor = ctx.GetService<ISwapExecutor>()
    let height =
      ctx.GetBlockHeight loopIn.PairIdValue.Quote
    task {
      match! actor.ExecNewLoopIn(loopIn, height) with
      | Ok resp ->
        return! json resp next ctx
      | Error e ->
        return! (error503 e) next ctx
    }

let private validateLoopIn (req: LoopInRequest) =
  fun (next: HttpFunc) (ctx: HttpContext) ->
    match req.Validate() with
    | Ok() ->
      next ctx
    | Error errorMsg ->
      errorBadRequest errorMsg next ctx

let handleLoopIn (loopIn: LoopInRequest) =
  (validateLoopIn loopIn
   >=> checkBlockchainIsSyncedAndSetTipHeight loopIn.PairIdValue
    >=> validateLoopInFeeLimitAgainstServerQuote loopIn
     >=> (handleLoopInCore loopIn))
