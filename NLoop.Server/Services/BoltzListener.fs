namespace NLoop.Server.Services


open System
open System.Collections.Concurrent
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open BoltzClient
open DotNetLightning.Utils
open FSharp.Control
open FSharp.Control.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

open Microsoft.Extensions.Options
open NBitcoin
open NLoop.Domain
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.SwapServerClient

type BoltzListener(swapServerClient: ISwapServerClient,
                   logger: ILogger<BoltzListener>,
                   opts: IOptions<NLoopOptions>,
                   actor: ISwapActor
                   ) =
  let statuses = ConcurrentDictionary<SwapId, Task<SwapId * Transaction>>()

  let mutable _executingTask = null
  let mutable _stoppingCts = null


  member this.ExecuteAsync(ct: CancellationToken) = unitTask {
      do! Task.Yield()
      try
        while not <| ct.IsCancellationRequested do
          ct.ThrowIfCancellationRequested()
          let ts = statuses.Values |> Seq.cast<_> |> Array.ofSeq
          if ts.Length = 0 then () else
          let! txAny = Task.WhenAny(ts)
          let! swapId, tx = txAny :?> Task<SwapId * Transaction>
          logger.LogInformation $"boltz notified about their swap tx ({tx.ToHex()}) for swap {swapId}"
          do! actor.Execute(swapId, Swap.Command.CommitSwapTxInfoFromCounterParty(tx.ToHex()), nameof(BoltzListener))
          statuses.TryRemove(swapId) |> ignore
      with
      | :? OperationCanceledException ->
        logger.LogInformation($"Stopping {nameof(BoltzListener)}...")
      | :? HttpRequestException as ex ->
        logger.LogCritical($"Connection to Boltz server {opts.Value.BoltzUrl} failed!")
        logger.LogError($"{ex}")
        raise <| ex
      | ex ->
        logger.LogError($"{ex}")
        raise <| ex
    }

  interface IHostedService with
    member this.StartAsync(ct) = unitTask {
      logger.LogInformation $"Starting {nameof(BoltzListener)}"
      try
        let! _ = swapServerClient.CheckConnection()
        ()
      with
      | ex ->
        logger.LogError $"Failed to connect to boltz-server {ex}"
        raise <| ex
      _stoppingCts <- CancellationTokenSource.CreateLinkedTokenSource(ct)
      _executingTask <- this.ExecuteAsync(_stoppingCts.Token)
      if _executingTask.IsCompleted then return! _executingTask else
      return ()
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

  interface ISwapEventListener with
    member this.RegisterSwap(swapId: SwapId, _group) =
      let t = task {
        let! tx = swapServerClient.ListenToSwapTx(swapId)
        return swapId, tx
      }
      statuses.TryAdd(swapId, t)
      |> ignore

    member this.RemoveSwap(swapId) =
      statuses.TryRemove(swapId)
      |> ignore

