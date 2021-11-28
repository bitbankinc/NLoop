namespace NLoop.Server

open System
open Microsoft.AspNetCore.Http
open Giraffe
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Options
open Microsoft.Extensions.Primitives
open NLoop.Domain
open NLoop.Domain.Utils
open NLoop.Server.Actors
open NLoop.Server.DTOs
open FSharp.Control.Reactive
open NLoop.Server.Projections
open NLoop.Server.RPCDTOs

module QueryHandlers =

  let handleGetInfo =
    fun (next: HttpFunc) (ctx: HttpContext) ->
      task {
        printfn $"opts: {ctx.GetService<IOptions<NLoopOptions>>().Value.RPCHost}"
        let _logger = ctx.GetLogger("handleGetInfo")
        let response = {
          GetInfoResponse.Version = Constants.AssemblyVersion
          SupportedCoins = { OnChain = [SupportedCryptoCode.BTC; SupportedCryptoCode.LTC]
                             OffChain = [SupportedCryptoCode.BTC] }
        }
        let! r = json response next ctx
        return r
      }
  let handleGetSwap (swapId: SwapId) =
    fun(next: HttpFunc) (ctx: HttpContext) -> task {
      let handler = ctx.GetService<ISwapActor>().Handler
      let! eventsR = handler.Replay swapId ObservationDate.Latest
      let stateR = eventsR |> Result.map handler.Reconstitute
      match stateR with
      | Error e ->
        return! error503 e next ctx
      | Ok s ->
        return! json s next ctx
    }

  let handleGetSwapHistory =
    fun(next: HttpFunc) (ctx: HttpContext) -> task {
      let resp: GetSwapHistoryResponse =
        ctx.GetService<SwapStateProjection>().State
        |> Map.toSeq
        |> Seq.choose(fun (streamId, v) ->
          let r =
            match v with
            | Swap.State.HasNotStarted -> None
            | Swap.State.Out(_height, { Cost = cost })
            | Swap.State.In(_height, { Cost = cost }) ->
              (streamId.Value, ShortSwapSummary.OnGoing cost) |> Some
            | Swap.State.Finished(cost, x) ->
              (streamId.Value, ShortSwapSummary.FromDomainState cost x) |> Some
          r
          |> Option.map(fun (streamId, s) ->
            if (streamId.StartsWith("swap-", StringComparison.OrdinalIgnoreCase)) then
              (streamId.Substring("swap-".Length), s)
            else
              (streamId, s)
          )
        )
        |> Map.ofSeq
      return! json resp next ctx
    }

  let handleGetOngoingSwap =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
      let resp: GetOngoingSwapResponse =
        ctx.GetService<SwapStateProjection>().State
        |> Map.toList
        |> List.choose(fun (_, v) ->
          match v with
          | Swap.State.Finished _ | Swap.State.HasNotStarted -> None
          | x -> Some x)
      return! json resp next ctx
    }

