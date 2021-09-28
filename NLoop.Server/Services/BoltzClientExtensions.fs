namespace NLoop.Server.Services

open FSharp.Control.Tasks
open System.Threading.Tasks
open System.Runtime.CompilerServices
open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Domain

type LoopOutQuoteRequest = {
  Amount: Money
  SweepConfTarget: BlockHeightOffset32
  pair: PairId
}

type LoopOutQuote = {
  SwapFee: Money
  PrepayAmount: Money
  MinerFee: Money
  SwapPaymentDest: PubKey
}

type LoopInQuoteRequest = {
  Amount: Money
  /// We don't need this since boltz does not require us to specify it.
  // HtlcConfTarget: BlockHeightOffset32
}

type LoopInQuote = {
  SwapFee: Money
  MinerFee: Money
  CltvDelta: BlockHeightOffset32
}

/// Extensions to treat boltz client in the same way with the lightning loop
[<AbstractClass;Sealed;Extension>]
type BoltzClientExtensions =
  [<Extension>]
  static member GetLoopOutQuote(this: BoltzClient, req: LoopOutQuoteRequest): Task<LoopOutQuote> = task {
    let! r = this.GetPairsAsync()
    let p = r.Pairs.[PairId.toString(&req.pair)]
    return {
      SwapFee =((p.Fees.Percentage / 100.) * (req.Amount.Satoshi |> double)) |> int64 |> Money.Satoshis
      PrepayAmount = failwith "todo"
      MinerFee = failwith "todo"
      SwapPaymentDest = failwith "todo" }
  }

  static member GetLoopInQuote(this: BoltzClient, req: LoopOutQuoteRequest): Task<LoopInQuote> = task {
    return failwith "todo"
  }
