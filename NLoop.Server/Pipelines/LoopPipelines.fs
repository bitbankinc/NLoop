module NLoop.Server.Pipelines.LoopPipelines

open Microsoft.Extensions.Logging
open NLoop.Server.DTOs
open NLoop.Server.Handlers.LoopHandlers
open NLoop.Server.Pipelines

open Microsoft.AspNetCore.Http
open FSharp.Control.Tasks
open Giraffe
    
let loopOutPipeline (req: LoopOutRequest) =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    let! r =
      handleLoopOut
        (ctx.GetService<_>())
        (ctx.GetService<_>())
        (ctx.GetService<_>())
        (ctx.GetService<_>())
        (ctx.GetService<_>())
        (ctx.GetLogger<ILogger>())
        req
    return! handleHandlerError next ctx r
  }


let loopInPipeline(req: LoopInRequest) =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    let! r =
      handleLoopIn
        (ctx.GetService<_>())
        (ctx.GetService<_>())
        (ctx.GetService<_>())
        req
    return! handleHandlerError next ctx r
  }