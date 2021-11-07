namespace NLoop.Server.Services

open System.Threading.Tasks
open DotNetLightning.Chain
open DotNetLightning.Utils
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.RPC
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.Actors

type BoltzFeeEstimator(boltzClient: BoltzClient) =
  interface IFeeEstimator with
    member this.Estimate _confTarget (cryptoCode) = task {
      let! feeMap = boltzClient.GetFeeEstimation()
      match feeMap.TryGetValue(cryptoCode.ToString()) with
      | true, fee ->
        return FeeRate(fee |> decimal)
      | false, _ ->
        return raise <| BoltzRPCException($"Boltz did not return feerate for cryptoCode {cryptoCode}! Supported CryptoCode was {feeMap |> Seq.map(fun k _ -> k) |> Seq.toList}")
    }

type BitcoinRPCBroadcaster(opts: IOptions<NLoopOptions>, logger: ILogger<BitcoinRPCBroadcaster>) =
  interface IBroadcaster with
    member this.BroadcastTx(tx, cryptoCode) = unitTask {
      let cli = opts.Value.GetRPCClient(cryptoCode)
      logger.LogInformation($"Broadcasting Transaction: {tx.GetWitHash()}")
      let! _ = cli.SendRawTransactionAsync(tx)
      ()
    }

type RPCFeeEstimator(opts: IOptions<NLoopOptions>) =
  interface IFeeEstimator with
    member this.Estimate target cc = task {
      let! resp = opts.Value.GetRPCClient(cc).TryEstimateSmartFeeAsync(target.Value |> int)
      return resp.FeeRate
    }

type BitcoinUTXOProvider(opts: IOptions<NLoopOptions>) =

  interface IUTXOProvider with
    member this.GetUTXOs(amount, cryptoCode) = task {
      let cli = opts.Value.GetRPCClient(cryptoCode)
      let! us = cli.ListUnspentAsync()
      let whatWeHave = us |> Seq.sumBy(fun u -> u.Amount)
      if whatWeHave < amount then return Error (UTXOProviderError.InsufficientFunds(whatWeHave, amount)) else
      return Ok (us |> Seq.map(fun u -> u.AsCoin() :> ICoin))
    }

    member this.SignSwapTxPSBT(psbt, cryptoCode) = task {
      let cli = opts.Value.GetRPCClient(cryptoCode)
      let! resp = cli.WalletProcessPSBTAsync(psbt, sign=true)
      return resp.PSBT
    }

