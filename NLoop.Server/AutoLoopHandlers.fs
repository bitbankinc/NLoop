module NLoop.Server.AutoLoopHandlers

open NLoop.Server.RPCDTOs
open Giraffe.Core
open FSharp.Control.Tasks
open Microsoft.AspNetCore.Http

let handleSetRule (req: SetRuleRequest): HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    return failwith "todo"
  }
