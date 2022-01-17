namespace NLoop.Server.Services


open System
open System.Collections.Concurrent
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
  let statuses = ConcurrentDictionary<SwapId, Task<Transaction>>()

  let mutable _executingTask = null
  let mutable _stoppingCts = null


  member this.ExecuteAsync(ct: CancellationToken) = unitTask {
      try
        while not <| ct.IsCancellationRequested do
          do! Task.Yield() // Without this, startup process hangs up by Task.WaitAny.
          ct.ThrowIfCancellationRequested()
          let ts = statuses.Values |> Seq.cast<_> |> Array.ofSeq
          let index = Task.WaitAny(ts, -1, ct)
          if index <> -1 then
            let! tx = ts.[index] :?> Task<Transaction>
            logger.LogInformation $"boltz notified about their swap tx {tx.ToHex()}"
            let swapId = statuses.Keys |> Seq.item index
            do! actor.Execute(swapId, Swap.Command.CommitSwapTxInfoFromCounterParty(tx.ToHex()))
            match statuses.TryRemove(swapId) with
            | true, _ -> ()
            | false, _ ->
              logger.LogWarning($"Failed to remove swap {swapId} this should never happen.")
      with
      | :? OperationCanceledException ->
        logger.LogInformation($"Stopping {nameof(BoltzListener)}...")
      | ex ->
        logger.LogCritical($"Connection to Boltz server {opts.Value.BoltzUrl} failed!")
        logger.LogError($"{ex}")
        raise <| ex
    }

  interface IHostedService with
    member this.StartAsync(ct) =
      logger.LogInformation $"Starting {nameof(BoltzListener)}"
      _stoppingCts <- CancellationTokenSource.CreateLinkedTokenSource(ct)
      _executingTask <- this.ExecuteAsync(_stoppingCts.Token)
      if _executingTask.IsCompleted then _executingTask else
      Task.CompletedTask

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
    member this.RegisterSwap(swapId: SwapId) =
      let t = swapServerClient.ListenToSwapTx(swapId)
      statuses.TryAdd(swapId, t)
      |> ignore

    member this.RemoveSwap(swapId) =
      statuses.TryRemove(swapId)
      |> ignore

