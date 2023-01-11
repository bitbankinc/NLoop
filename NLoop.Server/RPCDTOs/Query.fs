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
  
  [<JsonPropertyName "swap_txid">]
  SwapTxId: NBitcoin.uint256 option
  
  [<JsonPropertyName "swap_address">]
  SwapAddress: string option

  [<JsonPropertyName "cost">]
  Cost: SwapCost
}
  with
  static member OnGoing cost (swapTxInfo: UniversalSwapTxInfo)= {
    Type = OnGoing
    ErrorMsg = None
    RefundTxId = None
    SwapTxId = swapTxInfo.SwapTxId
    SwapAddress = Some(swapTxInfo.SwapAddress)
    Cost = cost
  }
  static member FromFinishedState cost (swapTxInfo: UniversalSwapTxInfo) (s: Swap.FinishedState) =
    let z = ShortSwapSummary.OnGoing cost swapTxInfo
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

[<Struct>]
type CostSummary = {
  [<JsonPropertyName "crypto_code">]
  CryptoCode: SupportedCryptoCode

  Cost: SwapCost
}

type GetCostSummaryResponse = {
  [<JsonPropertyName "server_endpoint">]
  ServerEndpoint: string

  [<JsonPropertyName "costs">]
  Costs: CostSummary[]
}
