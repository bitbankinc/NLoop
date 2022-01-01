namespace NLoop.Server.RPCDTOs

open System.Text.Json
open System.Text.Json.Serialization
open NLoop.Domain
open NLoop.Domain.IO

type ShortSwapSummaryType =
  | SuccessfullyFinished
  | FinishedByError
  | FinishedByRefund
  | OnGoing

type ShortSwapSummary = {
  [<JsonPropertyName "type">]
  Type: ShortSwapSummaryType
  [<JsonPropertyName "error_msg">]
  // [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
  ErrorMsg: string option
  [<JsonPropertyName "refund_txid">]
  // [<JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)>]
  RefundTxId: NBitcoin.uint256 option

  [<JsonPropertyName "cost">]
  Cost: SwapCost
}
  with
  static member OnGoing cost = {
    Type = OnGoing
    ErrorMsg = None
    RefundTxId = None
    Cost = cost
  }
  static member FromDomainState cost (s: Swap.FinishedState) =
    let z = ShortSwapSummary.OnGoing cost
    match s with
    | Swap.FinishedState.Success ->
      { z with Type = SuccessfullyFinished }
    | Swap.FinishedState.Errored e
    | Swap.FinishedState.Timeout e ->
      { z with Type = FinishedByError; ErrorMsg = Some e }
    | Swap.FinishedState.Refunded txid ->
      { z with Type = FinishedByRefund; RefundTxId = Some txid }

type GetSwapHistoryResponse = Map<string, ShortSwapSummary>
type GetOngoingSwapResponse = Swap.State list
