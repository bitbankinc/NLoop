namespace NLoop.Server

open System
open System.Net
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
open NLoop.Server.Services

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
      let handler = ctx.GetService<SwapActor>().Handler
      let! eventsR = handler.Replay(swapId) (ObservationDate.Latest)
      let stateR = eventsR |> Result.map handler.Reconstitute
      match stateR with
      | Error e ->
        return! error503 e next ctx
      | Ok s ->
        return! json (s) next ctx
    }

  let handleGetSwapHistory =
    fun(next: HttpFunc) (ctx: HttpContext) -> task {
      let state = ctx.GetService<SwapStateProjection>().State
      let resp: GetSwapHistoryResponse =
        state
        |> Map.toSeq
        |> Seq.choose(fun (streamId, v) ->
          let r =
            match v with
            | Swap.State.HasNotStarted -> None
            | Swap.State.Out(_height, _)
            | Swap.State.In(_height, _) ->
              (streamId.Value, ShortSwapSummary.OnGoing) |> Some
            | Swap.State.Finished x ->
              (streamId.Value, ShortSwapSummary.FromDomainState x) |> Some
          r
          |> Option.map(fun (streamId, s) ->
            if (streamId.StartsWith("swap-", StringComparison.OrdinalIgnoreCase)) then
              (streamId.Substring("swap-".Length), s)
            else
              (streamId, s)
          )
        )
        |> Map.ofSeq
      printfn $"Returning \n {resp} \nin json"
      return! json resp next ctx
    }

  let handleGetOngoingSwap =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
      let state = ctx.GetService<SwapStateProjection>().State
      let resp: GetOngoingSwapResponse =
        state
        |> Map.toList
        |> List.choose(fun (_, v) ->
          match v with
          | Swap.State.Finished _ | Swap.State.HasNotStarted -> None
          | x -> Some x)
      return! json resp next ctx
    }

  let handleListenEvent =
    fun (next : HttpFunc) (ctx : HttpContext) ->
      task {
        ctx.Response.Headers.Add("Cache-Control", "no-cache" |> StringValues)
        ctx.Response.Headers.Add("Content-Type", "text/event-stream" |> StringValues)
        do! ctx.Response.Body.FlushAsync()

        let e = ctx.GetService<IEventAggregator>().GetObservable<SwapEventWithId>()
        e
          |> Observable.flatmapTask(fun data -> task {
            do! ctx.Response.WriteAsJsonAsync({ SSEEvent.Data = data
                                                Id = Guid.NewGuid().ToString()
                                                Name = "TODO"
                                                Retry = None })
          })
          |> Observable.wait
        do! ctx.Response.WriteAsync("\n")
        do! ctx.Response.Body.FlushAsync()
        return! ctx.WriteTextAsync("Event Stream Finished")
      }
