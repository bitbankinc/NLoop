module NLoop.Server.Pipelines.AutoLoopPipelines

open Giraffe
open Microsoft.AspNetCore.Http
open NLoop.Domain

open NLoop.Server.Handlers.AutoLoopHandlers
open NLoop.Server.Pipelines
open NLoop.Server.RPCDTOs

let getLiquidityParamsPipeline (offChainAsset: SupportedCryptoCode) : HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    let! r =
      handleGetLiquidityParams
        (ctx.GetService<_>())
        offChainAsset
    return! handleHandlerError next ctx r
  }

let setLiquidityParamsPipeline
  (maybeOffchainAsset: SupportedCryptoCode option)
  (req: SetLiquidityParametersRequest): HttpHandler =
    fun next ctx -> task {
      let! r =
        handleSetLiquidityParams
          (ctx.GetService<_>())
          (ctx.GetService<_>())
          req
          (maybeOffchainAsset)
      match r with
      | Ok() ->
        if req.Parameters.AutoMaxInFlight > 2 then
          let msg = "autoloop is experimental, usually it is not good idea to set auto_max_inflight larger than 2"
          return! json {| warn = msg |} next ctx
        else
          return! json {||} next ctx
      | Error e ->
        return! handleHandlerError next ctx (Error e)
    }

let suggestSwapsPipeline (maybeOffchainAsset: SupportedCryptoCode option): HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    let! r =
      handleSuggestSwaps
        (ctx.GetService<_>())
        maybeOffchainAsset
    return! handleHandlerError next ctx r
  }
