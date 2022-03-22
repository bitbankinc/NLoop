namespace NLoop.Server

open System
open System.Threading
open EventStore.ClientAPI
open Microsoft.AspNetCore.Http
open Giraffe
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Options
open Microsoft.Extensions.Primitives
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server.Actors
open NLoop.Server.DTOs
open FSharp.Control.Reactive
open NLoop.Server.Projections
open NLoop.Server.RPCDTOs
open NLoop.Domain

module QueryHandlers =

  let handleGetInfo =
    fun (next: HttpFunc) (ctx: HttpContext) ->
      task {
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

  let private handleGetSwapHistoryCore (since: DateTime option) =
    fun(next: HttpFunc) (ctx: HttpContext) -> task {
      let actor = ctx.GetService<ISwapActor>()
      // todo: consider about cancellation
      match! actor.GetAllEntities(since) with
      | Error e ->
        return! error503 $"Failed to read events from DB\n {e}" next ctx
      | Ok entities ->
        let resp: GetSwapHistoryResponse =
          entities
          |> Map.toSeq
          |> Seq.choose(fun (streamId, v) ->
            (match v with
            | Swap.State.HasNotStarted -> None
            | Swap.State.Out(_height, { Cost = cost })
            | Swap.State.In(_height, { Cost = cost }) ->
              (streamId.Value, ShortSwapSummary.OnGoing cost) |> Some
            | Swap.State.Finished(cost, x, _) ->
              (streamId.Value, ShortSwapSummary.FromDomainState cost x) |> Some
            )
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

  let handleGetSwapHistory =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
      match ctx.TryGetDate("since") with
      | Some(Error e) ->
        return! errorBadRequest [e] next ctx
      | Some (Ok since) ->
        return! handleGetSwapHistoryCore (Some since) next ctx
      | None ->
        return! handleGetSwapHistoryCore None next ctx
    }

  let handleGetOngoingSwap =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
      let resp: GetOngoingSwapResponse =
        ctx.GetService<IOnGoingSwapStateProjection>().State
        |> Map.toList
        |> List.choose(fun (_, (_, v)) ->
          match v with
          | Swap.State.Finished _ | Swap.State.HasNotStarted -> None
          | x -> Some x)
      return! json resp next ctx
    }


  let private handleGetCostSummaryCore (since: DateTime option) =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
      let actor = ctx.GetService<ISwapActor>()
      match! actor.GetAllEntities since with
      | Error e ->
        return! error503 $"Failed to read events from DB\n {e}" next ctx
      | Ok entities ->
        let opts = ctx.GetService<GetOptions>()()
        let resp: GetCostSummaryResponse =
          entities
          |> Map.toSeq
          |> Seq.map snd
          |> SwapCost.foldSwapStates
          |> Map.toArray
          |> Array.map(fun (cc, cost) -> { CostSummary.Cost = cost; CryptoCode = cc })
          |> fun a -> { GetCostSummaryResponse.Costs = a; ServerEndpoint = opts.BoltzHost }
        return! json resp next ctx
    }

  // todo: cache with projection?
  let handleGetCostSummary =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
      match ctx.TryGetDate("since") with
      | Some(Error e) ->
        return! errorBadRequest [e] next ctx
      | Some (Ok since) ->
        return! handleGetCostSummaryCore (Some since) next ctx
      | None ->
        return! handleGetCostSummaryCore None next ctx
    }
