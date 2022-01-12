namespace NLoop.Server


open System
open System.Threading
open System.Threading.Tasks
open FSharp.Control.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open Microsoft.Extensions.Logging
open NLoop.Domain
open NLoop.Server

type RPCLongPollingBlockchainListener(opts: IOptions<NLoopOptions>,
                                      loggerFactory,
                                      getBlockchainClient,
                                      getRewindLimit,
                                      getNetwork,
                                      actor,
                                      cc) =
  inherit BlockchainListener(opts, loggerFactory, getBlockchainClient, cc, getNetwork, actor)
  let logger = loggerFactory.CreateLogger<RPCLongPollingBlockchainListener>()
  let mutable _executingTask = null
  let _stoppingCts = new CancellationTokenSource()

  member private this.ExecuteAsync(ct) = unitTask {
    while true do
      let client = getBlockchainClient(cc)
      let! tip = client.GetBestBlock(ct)
      do! this.OnBlock(tip.Block, getRewindLimit, ct)
      do! Task.Delay (TimeSpan.FromSeconds Constants.BlockchainLongPollingIntervalSec, ct)
  }

  interface IHostedService with
    member this.StartAsync(_cancellationToken) = unitTask {
      _executingTask <- this.ExecuteAsync(_stoppingCts.Token)
    }
    member this.StopAsync(_cancellationToken) = unitTask {
      if _executingTask = null then () else
      try
        _stoppingCts.Cancel()
      with
      | _ -> ()
      let! _ = Task.WhenAny(_executingTask, Task.Delay(Timeout.Infinite, _cancellationToken))
      ()
    }
