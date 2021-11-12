namespace NLoop.Server

open System.Threading
open FSharp.Control.Tasks
open System.Threading.Tasks
open System.Runtime.CompilerServices
open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Domain
open NLoop.Server.DTOs

[<RequireQualifiedAccess>]
type UnAcceptableQuoteError =
  | SweepFeeTooHigh of ourMax: Money * actual: Money
  | MinerFeeTooHigh of ourMax: Money * actual: Money
  | CLTVDeltaTooShort of ourMax: BlockHeightOffset32 * actual: BlockHeightOffset32
  with
  member this.Message =
    match this with
    | SweepFeeTooHigh(ourMax, actual) ->
      $"Sweep fee specified by the server is too high (ourMax: {ourMax}, The amount they specified: {actual}) "
    | MinerFeeTooHigh(ourMax, actual) ->
      $"Miner fee specified by the server is too high (ourMax: {ourMax}, The amount they specified {actual})"
    | CLTVDeltaTooShort(ourMax, actual) ->
      $"CLTVDelta they say they want to specify for their invoice is too short for our HTLCConfirmation parameter.
      (our acceptable max is {ourMax}, actual value is {actual})
      "

type LoopOutQuoteRequest = {
  Amount: Money
  SweepConfTarget: BlockHeightOffset32
  pair: PairId
}

/// estimates for the fees making up the total swap cost for the client.
type LoopOutQuote = {
  SwapFee: Money
  MinerFee: Money
  SwapPaymentDest: PubKey
  CltvDelta: BlockHeightOffset32
  PrepayAmount: Money
}
  with
  member this.Validate(limits: LoopOutLimits) =
    if this.SwapFee > limits.MaxSwapFee then
      (limits.MaxSwapFee, this.SwapFee) |> UnAcceptableQuoteError.SweepFeeTooHigh |> Error
    elif this.MinerFee > limits.MaxMinerFee then
      (limits.MaxMinerFee, this.MinerFee) |> UnAcceptableQuoteError.MinerFeeTooHigh |> Error
    else
      let ourAcceptableMaxHTLCConf = limits.HTLCConfTarget + Swap.Constants.DefaultSweepConfTargetDelta
      if this.CltvDelta > ourAcceptableMaxHTLCConf then
        (ourAcceptableMaxHTLCConf, this.CltvDelta) |> UnAcceptableQuoteError.CLTVDeltaTooShort |> Error
      else
        Ok ()

type LoopInQuoteRequest = {
  Amount: Money
  /// We don't need this since boltz does not require us to specify it.
  // HtlcConfTarget: BlockHeightOffset32
}

type LoopInQuote = {
  SwapFee: Money
  MinerFee: Money
}
  with
  member this.Validate(feeLimit: LoopInLimits) =
    if this.SwapFee > feeLimit.MaxSwapFee then
      (feeLimit.MaxSwapFee, this.SwapFee) |> UnAcceptableQuoteError.SweepFeeTooHigh |> Error
    elif this.MinerFee > feeLimit.MaxMinerFee then
      (feeLimit.MaxMinerFee, this.MinerFee) |> UnAcceptableQuoteError.MinerFeeTooHigh |> Error
    else
      Ok ()

/// Extensions to treat boltz client in the same way with the lightning loop
[<AbstractClass;Sealed;Extension>]
type BoltzClientExtensions =
  [<Extension>]
  static member GetLoopOutQuote(this: BoltzClient, req: LoopOutQuoteRequest, ?ct: CancellationToken): Task<LoopOutQuote> = task {
    let ct = defaultArg ct CancellationToken.None
    let! r = this.GetPairsAsync(ct)
    let p = r.Pairs.[PairId.toString(&req.pair)]
    let! nodes = this.GetNodesAsync(ct)
    return {
      SwapFee =((p.Fees.Percentage / 100.) * (req.Amount.Satoshi |> double)) |> int64 |> Money.Satoshis
      MinerFee =
        p.Fees.MinerFees.BaseAsset.Normal |> Money.Satoshis
      SwapPaymentDest =
        nodes.Nodes |> Seq.head |> fun i -> i.Value.NodeKey
      CltvDelta = p.Limits.MaxCLTVDelta |> uint32 |> BlockHeightOffset32
      PrepayAmount = failwith "todo"
    }
  }

  [<Extension>]
  static member GetLoopInQuote(this: BoltzClient, req: LoopInQuoteRequest): Task<LoopInQuote> = task {
    return failwith "todo"
  }
