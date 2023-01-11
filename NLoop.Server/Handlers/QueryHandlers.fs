namespace NLoop.Server.Handlers

open System
open Microsoft.AspNetCore.Http
open FSharp.Control.Tasks.Affine
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils

open NLoop.Server
open NLoop.Server.DTOs
open NLoop.Server.Projections
open NLoop.Server.RPCDTOs
module QueryHandlers =
  let handleGetInfo =
      {
        GetInfoResponse.Version = Constants.AssemblyVersion
        SupportedCoins = { OnChain = [SupportedCryptoCode.BTC; SupportedCryptoCode.LTC]
                           OffChain = [SupportedCryptoCode.BTC] }
      }

  let handleGetSwap(actor: ISwapActor) swapId =
    task {
      let handler = actor.Handler
      let! eventsR = handler.Replay swapId ObservationDate.Latest
      return
        eventsR
        |> Result.map handler.Reconstitute
        |> Result.mapError HandlerError.FromEventSourcingError
    }
      
  let handleGetSwapHistory (actor: ISwapActor) since =
    task {
      // todo: consider about cancellation
      match! actor.GetAllEntities(since) with
      | Error e ->
        return Error (HandlerError.InternalError $"Failed to read events from DB\n {e}")
      | Ok entities ->
        let resp: GetSwapHistoryResponse =
          entities
          |> Map.toSeq
          |> Seq.choose(fun (streamId, v) ->
            (match v with
            | Swap.State.HasNotStarted -> None
            | Swap.State.Out(_height, state) ->
              let cost = state.Cost
              (streamId.Value, ShortSwapSummary.OnGoing cost state.UniversalSwapTxInfo) |> Some
            | Swap.State.In(_height, state) ->
              let cost = state.Cost
              (streamId.Value, ShortSwapSummary.OnGoing cost state.UniversalSwapTxInfo) |> Some
            | Swap.State.Finished(cost, swapTxInfo, x, _) ->
              (streamId.Value, ShortSwapSummary.FromFinishedState cost swapTxInfo x) |> Some
            )
            |> Option.map(fun (streamId, s) ->
              if (streamId.StartsWith("swap-", StringComparison.OrdinalIgnoreCase)) then
                (streamId.Substring("swap-".Length), s)
              else
                (streamId, s)
            )
          )
          |> Map.ofSeq
        return Ok resp
    }

  let handleGetOngoingSwap (proj: IOnGoingSwapStateProjection) =
    let resp: GetOngoingSwapResponse =
      proj.State
      |> Map.toList
      |> List.choose(fun (_, (_, v)) ->
        match v with
        | Swap.State.Finished _ | Swap.State.HasNotStarted -> None
        | x -> Some x)
    resp
    
    
  let handleGetCostSummary (since: DateTime option) (actor: ISwapActor) (getOpts: GetOptions) =
    task {
      match! actor.GetAllEntities since with
      | Error e ->
        return Error (HandlerError.InternalError $"Failed to read events from DB\n {e}")
      | Ok entities ->
        let resp: GetCostSummaryResponse =
          entities
          |> Map.toSeq
          |> Seq.map snd
          |> SwapCost.foldSwapStates
          |> Map.toArray
          |> Array.map(fun (cc, cost) -> { CostSummary.Cost = cost; CryptoCode = cc })
          |> fun a -> { GetCostSummaryResponse.Costs = a; ServerEndpoint = getOpts().BoltzHost }
        return Ok resp
    }
