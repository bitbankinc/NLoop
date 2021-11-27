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
  Pair: PairId
}

/// estimates for the fees making up the total swap cost for the client.
type LoopOutQuote = {
  SwapFee: Money
  SweepMinerFee: Money
  SwapPaymentDest: PubKey
  CltvDelta: BlockHeightOffset32
  PrepayAmount: Money
}
  with
  member this.Validate(limits: LoopOutLimits) =
    if this.SwapFee > limits.MaxSwapFee then
      (limits.MaxSwapFee, this.SwapFee) |> UnAcceptableQuoteError.SweepFeeTooHigh |> Error
    elif this.SweepMinerFee > limits.MaxMinerFee then
      (limits.MaxMinerFee, this.SweepMinerFee) |> UnAcceptableQuoteError.MinerFeeTooHigh |> Error
    else
      let ourAcceptableMaxHTLCConf = limits.SwapTxConfRequirement + Swap.Constants.DefaultSweepConfTargetDelta
      if this.CltvDelta > ourAcceptableMaxHTLCConf then
        (ourAcceptableMaxHTLCConf, this.CltvDelta) |> UnAcceptableQuoteError.CLTVDeltaTooShort |> Error
      else
        Ok ()

type LoopInQuoteRequest = {
  Amount: Money
  /// We don't need this since boltz does not require us to specify it.
  // HtlcConfTarget: BlockHeightOffset32
  Pair: PairId
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

type OutTermsResponse = {
  MinSwapAmount: Money
  MaxSwapAmount: Money
}

type InTermsResponse = {
  MinSwapAmount: Money
  MaxSwapAmount: Money
}


[<AutoOpen>]
module private BoltzClientExtensionsHelpers =
  let inline percentToSat (amount: Money, percent: double) =
    (amount.Satoshi * (percent / 100. |> int64)) |> Money.Satoshis

/// Extensions to treat boltz client in the same way with the lightning loop
[<AbstractClass;Sealed;Extension>]
type BoltzClientExtensions =

  [<Extension>]
  static member GetLoopOutQuote(this: BoltzClient, req: LoopOutQuoteRequest, ?ct: CancellationToken): Task<LoopOutQuote> = task {
    let ct = defaultArg ct CancellationToken.None
    let! r = this.GetPairsAsync(ct)
    let ps = PairId.toString(&req.Pair)
    let p = r.Pairs.[ps]
    let! nodes = this.GetNodesAsync(ct)
    let! timeoutResponse = this.GetTimeoutsAsync(ct)
    return {
      SwapFee =
        // boltz fee is returned with percentage, we have to convert to absolute value.
        percentToSat(req.Amount, p.Fees.Percentage)
      SweepMinerFee =
        p.Fees.MinerFees.BaseAsset.Reverse.Claim |> Money.Satoshis
      SwapPaymentDest =
        nodes.Nodes |> Seq.head |> fun i -> i.Value.NodeKey
      CltvDelta = timeoutResponse.Timeouts.[ps].Quote |> uint |> BlockHeightOffset32
      PrepayAmount =
        // In boltz, what we have to pay as `prepay.minerfee` always equals to their (estimated) lockup tx fee
        p.Fees.MinerFees.BaseAsset.Reverse.Lockup |> Money.Satoshis
    }
  }

  [<Extension>]
  static member GetLoopInQuote(this: BoltzClient, req: LoopInQuoteRequest, ?ct: CancellationToken): Task<LoopInQuote> = task {
    let ct = defaultArg ct CancellationToken.None
    let! r = this.GetPairsAsync(ct)
    let ps = PairId.toString(&req.Pair)
    let p = r.Pairs.[ps]
    return {
      LoopInQuote.MinerFee = p.Fees.MinerFees.QuoteAsset.Normal |> Money.Satoshis
      SwapFee =
        percentToSat(req.Amount, p.Fees.Percentage)
    }
  }

  [<Extension>]
  static member GetLoopOutTerms(this: BoltzClient, pairId: PairId, ?ct: CancellationToken) = task {
    let ct = defaultArg ct CancellationToken.None
    let! getPairsResponse = this.GetPairsAsync(ct)
    let ps = PairId.toString(&pairId)
    let p = getPairsResponse.Pairs.[ps]
    return {
      OutTermsResponse.MaxSwapAmount = p.Limits.Maximal |> Money.Satoshis
      MinSwapAmount = p.Limits.Minimal |> Money.Satoshis
    }
  }

  [<Extension>]
  static member GetLoopInTerms(this: BoltzClient, pairId: PairId, ?ct: CancellationToken) = task {
    let ct = defaultArg ct CancellationToken.None
    let! getPairsResponse = this.GetPairsAsync(ct)
    let ps = PairId.toString(&pairId)
    let p = getPairsResponse.Pairs.[ps]
    return {
      InTermsResponse.MaxSwapAmount = p.Limits.Maximal |> Money.Satoshis
      MinSwapAmount = p.Limits.Minimal |> Money.Satoshis
    }
  }
