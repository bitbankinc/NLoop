module NLoop.Server.AutoLoopHandlers

open System
open System.Threading.Tasks
open FSharp.Control.Tasks
open Microsoft.AspNetCore.Http
open Giraffe
open Giraffe.Core
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server.Actors
open NLoop.Server.RPCDTOs
open FsToolkit.ErrorHandling

let handleSetRule (req: SetRuleRequest): HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    return failwith "todo"
  }
let handleSetRule2 (req: SetRuleRequest): HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    return failwith "todo"
  }
