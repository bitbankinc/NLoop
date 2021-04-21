namespace NLoop.Server.Services

open System.Threading.Tasks
open DotNetLightning.Chain
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Options
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server

type BoltzFeeEstimator(boltzClientProvider: BoltzClientProvider, opts: IOptions<NLoopOptions>) =
  interface IFeeEstimator with
    member this.Estimate(cryptoCode) = task {
      let cli = boltzClientProvider.Invoke(opts.Value.GetNetwork(cryptoCode))
      let! feeMap = cli.GetFeeEstimation()
      match feeMap.TryGetValue(cryptoCode.ToString()) with
      | true, fee ->
        return FeeRate(fee |> decimal)
      | false, _ ->
        return raise <| BoltzRPCException($"Boltz did not return feerate for cryptoCode {cryptoCode}! Supported CryptoCode was {feeMap |> Seq.map(fun k _ -> k) |> Seq.toList}")
    }

type BitcoinRPCBroadcaster(opts: IOptions<NLoopOptions>) =
  interface IBroadcaster with
    member this.BroadcastTx(tx, cryptoCode) = unitTask {
      let cli = opts.Value.GetRPCClient(cryptoCode)
      let! _ = cli.SendRawTransactionAsync(tx)
      ()
    }
