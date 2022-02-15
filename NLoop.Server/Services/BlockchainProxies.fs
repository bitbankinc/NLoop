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
      logger.LogInformation("Broadcasting Transaction. Id: {TxHash}", tx.GetHash())
      logger.LogDebug("Broadcasting Transaction. {Tx}", tx.ToHex())
      try
        let! _ = cli.SendRawTransaction(tx)
        ()
      with
      | :? RPCException as ex when ex.Message.Contains "insufficient fee, rejecting replacement" ->
        // failed to bump in RBF, do nothing.
        logger.LogWarning("Failed to bump {Tx}, insufficient fee,", tx.ToHex())
        ()
      | :? RPCException as ex when ex.Message.Contains "txn-mempool-conflict" ->
        // same tx already in mempool, when RBF disabled, do nothing.
        logger.LogWarning("Failed to broadcast {Tx}, tx already in mempool", tx.ToHex())
        ()
      | :? RPCException as ex when ex.Message.Contains "Transaction already in block" ->
        // same tx already in the chain, do nothing.
        logger.LogInformation("Failed to broadcast {Tx}, tx already in mempool", tx.ToHex())
        ()
    }

type RPCFeeEstimator(getClient: GetBlockchainClient, logger: ILogger<RPCFeeEstimator>) =
  interface IFeeEstimator with
    member this.Estimate target cc = task {
      try
        return! getClient(cc).EstimateFee(target)
      with
      | :? NoEstimationException as ex ->
        logger.LogWarning $"Failed estimate fee for {cc}: (target blockcount: {target}). using fallback fee. ({ex.Message})"
        return
          Constants.FallbackFeeSatsPerByte
          |> decimal
          |> FeeRate
    }

