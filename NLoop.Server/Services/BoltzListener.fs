namespace NLoop.Server.Services


open System
open System.Collections.Concurrent
open System.Net.Http
open System.Threading
open System.Threading.Channels
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
                   opts: GetOptions,
                   actor: ISwapActor
                   ) =
  let statuses = ConcurrentDictionary<SwapId, Task>()

  let updateQueue = Channel.CreateBounded<SwapId * Transaction>(10)

  let mutable _executingTask = null
  let mutable _stoppingCts = null

  member this.ExecuteAsync(ct: CancellationToken) = unitTask {
      do! Task.Yield()
      try
        let mutable finished = false
        while not <| ct.IsCancellationRequested && not <| finished do
          let! channelIsAlive = updateQueue.Reader.WaitToReadAsync(ct)
          finished <- not <| channelIsAlive
          if not <| finished then
            let! swapId, tx = updateQueue.Reader.ReadAsync(ct)
            logger.LogInformation $"boltz notified about their swap tx ({tx.ToHex()}) for swap {swapId}"
            do! actor.Execute(swapId, Swap.Command.CommitSwapTxInfoFromCounterParty(tx.ToHex()), nameof(BoltzListener), true)
            statuses.TryRemove(swapId) |> ignore
      with
      | :? OperationCanceledException ->
        logger.LogInformation($"Stopping {nameof(BoltzListener)}...")
      | :? HttpRequestException as ex ->
        logger.LogCritical($"Connection to Boltz server {opts().BoltzUrl} failed!")
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
        let! _ = swapServerClient.CheckConnection(ct)
        ()
      with
      | ex ->
        logger.LogError $"Failed to connect to boltz-server {ex}"
        raise <| ex
      _stoppingCts <- new CancellationTokenSource()
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
      logger.LogDebug $"registering the swap {swapId}"
      let t = task {
        let onTransaction (tx: Transaction) =
          task {
            try
              let! channelOpened = updateQueue.Writer.WaitToWriteAsync()
              if channelOpened then
                do! updateQueue.Writer.WriteAsync((swapId, tx))
            with
            | ex ->
              logger.LogWarning($"error while handling swap {ex}")
          } :> Task
        do! swapServerClient.ListenToSwapTx(swapId, onTransaction)
      }
      statuses.TryAdd(swapId, t)
      |> ignore

    member this.RemoveSwap(swapId) =
      logger.LogDebug $"removing the swap {swapId}"
      statuses.TryRemove(swapId)
      |> ignore

