namespace NLoop.Server.Services

open System.Threading.Tasks
open NBitcoin

type IFeeEstimator =
  abstract member Estimate: unit -> Task<FeeRate>

type BitcoinRPCFeeEstimator() =
  interface IFeeEstimator with
    member this.Estimate() =
      failwith "todo"
