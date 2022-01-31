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
      | :? NoEstimationException as ex ->
        logger.LogWarning $"Failed estimate fee for {cc}: (target blockcount: {target}). using fallback fee. ({ex.Message})"
        return
          Constants.FallbackFeeSatsPerByte
          |> decimal
          |> FeeRate
    }

