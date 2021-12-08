namespace NLoop.Server.Services

open System
open System.Threading.Tasks
open DotNetLightning.Utils
open FSharp.Control.Tasks
open FsToolkit.ErrorHandling
open LndClient
open Microsoft.Extensions.Logging
open NBitcoin
open NLoop.Domain
open NLoop.Server
open NLoop.Server.RPCDTOs
open NLoop.Server.SwapServerClient


module Pipelines =

  type State = {
    Parameters: Parameters
    Config: Config
  }

  type GlobalState = {
    States: Map<Swap.Group, State>
  }

  type Deps = {
    Logger: ILogger
    SwapServerClient: ISwapServerClient
  }

  let singleReasonSuggestion reason (p: Parameters) =
    let r = p.Rules
    { SwapSuggestions.Zero
        with
        DisqualifiedChannels = r.ChannelRules |> Map.map(fun _ _ -> reason)
        DisqualifiedPeers = r.PeerRules |> Map.map(fun _ _ -> reason)
    }

  type Errors =
    | AutoLoopError of AutoLoopError
    | SingleReasonError of SwapDisqualifiedReason

  let private checkAutoLoopStarted(p: Parameters) =
    if p.AutoFeeStartDate < DateTimeOffset.UtcNow then Ok() else
    Error <| SingleReasonError(SwapDisqualifiedReason.BudgetNotStarted)

  let private checkAgainstFeeMarket
    (cc: SupportedCryptoCode)
    ({ Config.EstimateFee = f })
    { Parameters.SweepConfTarget = confTarget
      FeeLimit = feeLimit } = task {
    let! resp = f.Estimate(confTarget) cc
    return feeLimit.CheckWithEstimatedFee(resp)
  }

  let private getSwapRestrictions group (swapServerClient: ISwapServerClient) (par: Parameters) = taskResult {
      let isZeroConf = par.SwapTxConfRequirement = BlockHeightOffset32.Zero
      let! restrictions = swapServerClient.GetSwapAmountRestrictions(group, isZeroConf)
      do!
          ServerRestrictions.Validate(
            restrictions,
            par.ClientRestrictions
          )
          |> Result.mapError(AutoLoopError.RestrictionError)
      return
        {
          Minimum =
            match par.ClientRestrictions.Minimum with
            | Some min when min > restrictions.Minimum ->
              min
            | _ -> restrictions.Minimum
          Maximum =
            match par.ClientRestrictions.Maximum with
            | Some max when max < restrictions.Maximum ->
              max
            | _ -> restrictions.Maximum
        }
  }

  let suggestSwaps { Deps.SwapServerClient = swapServerClient } (group: Swap.Group) (state: State): Task<Result<SwapSuggestions, Errors>> = taskResult {
    if not <| state.Parameters.HaveRules then return! AutoLoopError.NoRules |> AutoLoopError |> Error else
    do! checkAutoLoopStarted(state.Parameters)
    match group.Category with
    | Swap.Category.Out ->
      do! checkAgainstFeeMarket group.OnChainAsset state.Config state.Parameters
          |> TaskResult.mapError(SingleReasonError)
    | _ -> ()
    let restrictions = getSwapRestrictions(group)
    return failwith "todo"
  }

  let private handleSuggestions (s: SwapSuggestions) =
    ()

  type Event = unit

  let exec ({ Deps.Logger = logger; } as deps) (gs: GlobalState) pairId : Task<Event> = task {
    for kv in gs.States do
      match! suggestSwaps deps kv.Key kv.Value with
      | Ok suggestions ->
        handleSuggestions suggestions
      | Error (SingleReasonError e) ->
        singleReasonSuggestion e kv.Value.Parameters |> handleSuggestions
      | Error(AutoLoopError AutoLoopError.NoRules) ->
        ()
      | Error e ->
        logger.LogError($"Error in autoloop: {e}")
        ()
    ()
  }
