module NLoop.Server.Pipelines.QueryPipelines

open System
open Microsoft.AspNetCore.Http
open Giraffe
open NLoop.Domain

open NLoop.Server
open NLoop.Server.Projections
open NLoop.Server.Handlers.QueryHandlers
open NLoop.Server.Pipelines

let getInfoPipeline =
  fun (next: HttpFunc) (ctx: HttpContext) ->
    task {
      let response = handleGetInfo
      return! json response next ctx
    }
    
let getSwapPipeline (swapId: SwapId) =
  fun(next: HttpFunc) (ctx: HttpContext) -> task {
    let! r =
      handleGetSwap(ctx.GetService<ISwapActor>()) swapId
    return! handleHandlerError next ctx r
  }
  
let getOngoingSwapPipeline =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    let resp =
      ctx.GetService<IOnGoingSwapStateProjection>()
      |> handleGetOngoingSwap
    return! json resp next ctx
  }

let private getSwapHistoryPipelineCore (since: DateTime option) =
  fun(next: HttpFunc) (ctx: HttpContext) -> task {
      let actor = ctx.GetService<ISwapActor>()
      let! r = handleGetSwapHistory(actor) since
      return! handleHandlerError next ctx r
  }

let getSwapHistoryPipeline =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    match ctx.TryGetDate("since") with
    | Some(Error e) ->
      return! errorBadRequest [e] next ctx
    | Some (Ok since) ->
      return! getSwapHistoryPipelineCore (Some since) next ctx
    | None ->
      return! getSwapHistoryPipelineCore None next ctx
  }

let private getCostSummaryPipelineCore (since: DateTime option) =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    let actor = ctx.GetService<ISwapActor>()
    let getOpts = ctx.GetService<GetOptions>()
    let! r = handleGetCostSummary since actor getOpts
    return! handleHandlerError next ctx r
  }

// todo: cache with projection?
let getCostSummaryPipeline =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    match ctx.TryGetDate("since") with
    | Some(Error e) ->
      return! errorBadRequest [e] next ctx
    | Some (Ok since) ->
      return! getCostSummaryPipelineCore (Some since) next ctx
    | None ->
      return! getCostSummaryPipelineCore None next ctx
  }
