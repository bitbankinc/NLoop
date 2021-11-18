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

let getLiquidityParams : HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    let resp = {
      LiquidityParameters.Rules = [||]
      FeePPM = failwith "todo"
      SweepFeeRateSatPerVByte = failwith "todo"
      MaxSwapFeePpm = failwith "todo"
      MaxRoutingFeePpm = failwith "todo"
      MaxPrepayRoutingFeePpm = failwith "todo"
      MaxPrepay = failwith "todo"
      MaxMinerFee = failwith "todo"
      SweepConfTarget = failwith "todo"
      FailureBackoffSecond = failwith "todo"
      AutoLoop = failwith "todo"
      AutoLoopBudget = failwith "todo"
      AutoLoopBudgetStartSecond = failwith "todo"
      AutoMaxInFlight = failwith "todo"
      MinSwapAmount = failwith "todo"
      MaxSwapAmount = failwith "todo" }
    return! json resp next ctx
  }


let setLiquidityParams(parameters: SetLiquidityParametersRequest): HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    return! json {||} next ctx
  }

let suggestSwaps: HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    let resp = {
      SuggestSwapsResponse.Disqualified = [||]
      LoopOut = failwith "todo"
      LoopIn = failwith "todo"
    }
    return! json resp next ctx
  }
