namespace NLoop.Server.Services

open System.Threading
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

type RPCFeeEstimator(getClient: GetBlockchainClient, logger: ILogger<RPCFeeEstimator>) =
  interface IFeeEstimator with
    member this.Estimate target cc = task {
      try
        return! getClient(cc).EstimateFee(target)
      with
      | :? RPC.NoEstimationException as ex ->
        logger.LogWarning $"Failed estimate fee for {cc}: (target blockcount: {target}). using fallback fee. ({ex.Message})"
        return
          Constants.FallbackFeeSatsPerByte
          |> decimal
          |> FeeRate
    }

type BitcoinUTXOProvider(getWalletClient: GetWalletClient, opts: IOptions<NLoopOptions>) =

  interface IUTXOProvider with
    member this.GetUTXOs(amount, cryptoCode) = task {
      let cli = getWalletClient(cryptoCode)
      let network = opts.Value.GetNetwork(cryptoCode)
      let! us = cli.ListUnspent(network, CancellationToken.None)
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

