namespace NLoop.Server


open System
open System.Threading
open System.Threading.Tasks
open FSharp.Control.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open Microsoft.Extensions.Logging
open NLoop.Server

type RPCLongPollingBlockchainListener(opts: IOptions<NLoopOptions>, loggerFactory, getBlockchainClient, actor) =
  inherit BlockchainListener(opts, loggerFactory, getBlockchainClient, actor)
  let logger = loggerFactory.CreateLogger<RPCLongPollingBlockchainListener>()
  let mutable _executingTask = null
  let _stoppingCts = new CancellationTokenSource()

  member private this.Worker(cc, ct) = unitTask {
    let client = getBlockchainClient(cc)
    let! tip = client.GetBestBlock(ct)
    do! this.OnBlock(cc, tip.Block, ct)
  }

  member private this.ExecuteAsync(ct) = unitTask {
    for cc in opts.Value.OffChainCrypto do
      do! this.Worker(cc, ct)
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
