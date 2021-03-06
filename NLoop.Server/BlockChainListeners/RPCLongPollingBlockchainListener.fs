namespace NLoop.Server


open System
open System.Threading
open System.Threading.Tasks
open FSharp.Control.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open Microsoft.Extensions.Logging
open NBitcoin.RPC
open NLoop.Domain
open NLoop.Server

type RPCLongPollingBlockchainListener(
                                      loggerFactory,
                                      getBlockchainClient,
                                      getRewindLimit,
                                      getNetwork,
                                      actor,
                                      cc) =
  inherit BlockchainListener(loggerFactory, getBlockchainClient, cc, getNetwork, actor)
  let logger = loggerFactory.CreateLogger<RPCLongPollingBlockchainListener>()
  let mutable _executingTask = null
  let mutable _stoppingCts = new CancellationTokenSource()
  let mutable client: IBlockChainClient option = None

  member private this.ExecuteAsync(ct: CancellationToken) = unitTask {
    let mutable count = 0
    while not <| ct.IsCancellationRequested do
      try
        let! tip = client.Value.GetBestBlock(ct)
        if count % 20 = 0 then
          logger.LogDebug $"long-polling blockchain {cc}, got tip: {tip}"
        do! this.OnBlock(tip.Block, getRewindLimit, ct)
        do! Task.Delay (TimeSpan.FromSeconds Constants.BlockchainLongPollingIntervalSec, ct)
        count <- count + 1
      with
      | :? OperationCanceledException ->
        logger.LogDebug $"operation canceled. stopping {nameof(RPCLongPollingBlockchainListener)} ..."
        return ()
      | ex ->
        logger.LogError(ex, "Error when getting the best block from the Blockchain ({CryptoCode})", cc)
        do! Task.Delay (TimeSpan.FromSeconds Constants.BlockchainLongPollingIntervalSec, ct)

    logger.LogDebug $"operation canceled. stopping {nameof(RPCLongPollingBlockchainListener)} ..."
    return ()
  }

  interface IHostedService with
    member this.StartAsync(_cancellationToken) = unitTask {
      try
        client <- getBlockchainClient(cc) |> Some
        // let! _ = client.Value.GetBlockChainInfo(_cancellationToken)
        let! bestBlock = client.Value.GetBestBlock(_cancellationToken)
        logger.LogInformation $"CurrentTip: {bestBlock}"
        this.CurrentTip <- bestBlock
        ()
      with
      | :? RPCException as ex ->
        let msg =
          $"Failed to connect to the blockchain daemon for {cc}. " +
          "check your settings, or drop the support for this crypto by specifying "+
          $"--{nameof(NLoopOptions.Instance.OnChainCrypto).ToLowerInvariant()}"
        logger.LogError msg
        raise <| ex
      | :? FormatException as ex ->
        let msg =
          $"Failed to get an rpc settings for {cc}. " +
          "check your settings, or drop the support for this crypto by specifying "+
          $"--{nameof(NLoopOptions.Instance.OnChainCrypto).ToLowerInvariant()}"
        logger.LogError msg
        raise <| ex
        ()
      _executingTask <- this.ExecuteAsync(_stoppingCts.Token)
      if _executingTask.IsCompleted then return! _executingTask else
      return ()
    }
    member this.StopAsync(_cancellationToken) = unitTask {
      logger.LogInformation $"Stopping {nameof(RPCLongPollingBlockchainListener)} ..."
      if _executingTask = null then () else
      try
        _stoppingCts.Cancel()
      with
      | _ -> ()
      let! _ = Task.WhenAny(_executingTask, Task.Delay(Timeout.Infinite, _cancellationToken))
      ()
    }
