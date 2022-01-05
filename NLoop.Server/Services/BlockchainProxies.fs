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
open NLoop.Server.SwapServerClient

type BitcoinRPCBroadcaster(getClient: GetBlockchainClient, logger: ILogger<BitcoinRPCBroadcaster>) =
  interface IBroadcaster with
    member this.BroadcastTx(tx, cryptoCode) = unitTask {
      let cli = getClient(cryptoCode)
      logger.LogInformation($"Broadcasting Transaction: {tx.GetWitHash()}")
      let! _ = cli.SendRawTransaction(tx)
      ()
    }

type RPCFeeEstimator(getClient: GetBlockchainClient) =
  interface IFeeEstimator with
    member this.Estimate target cc = task {
      return! getClient(cc).EstimateFee(target)
    }

type BitcoinUTXOProvider(getWalletClient: GetWalletClient) =

  interface IUTXOProvider with
    member this.GetUTXOs(amount, cryptoCode) = task {
      let cli = getWalletClient(cryptoCode)
      let! us = cli.ListUnspent()
      let whatWeHave = us |> Seq.sumBy(fun u -> u.Amount)
      if whatWeHave < amount then
        return
          (cryptoCode, whatWeHave, amount)
          |> UTXOProviderError.InsufficientFunds
          |> Error
      else
        return Ok (us |> Seq.map(fun u -> u.AsCoin() :> ICoin))
    }

    member this.SignSwapTxPSBT(psbt, cryptoCode) =
      let cli = getWalletClient(cryptoCode)
      cli.SignSwapTxPSBT(psbt)

