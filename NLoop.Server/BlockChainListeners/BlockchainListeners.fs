namespace NLoop.Server

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading.Tasks
open FSharp.Control.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Server.Options


type BlockchainListeners(opts: IOptions<NLoopOptions>, loggerFactory: ILoggerFactory, getBlockchainClient, swapActor) =
  let mutable listeners = ConcurrentDictionary()
  let logger = loggerFactory.CreateLogger<BlockchainListeners>()

  let [<Literal>] backoffLimit = 6
  interface IHostedService with
    member this.StartAsync(ct) = unitTask {
      logger.LogInformation $"Starting blockchain listeners ..."

      let roundTrip cc = unitTask {
        let cOpts = opts.Value.ChainOptions.[cc]
        let startRPCListener cc = task {
          let rpcListener =
            RPCLongPollingBlockchainListener(opts, loggerFactory, getBlockchainClient, swapActor, cc)
          do! (rpcListener :> IHostedService).StartAsync(ct)
          match listeners.TryAdd(cc, rpcListener :> BlockchainListener) with
          | true -> ()
          | false ->
            logger.LogError($"Failed to add {nameof(RPCLongPollingBlockchainListener)} ({cc})")
        }
        match cOpts.TryGetZmqAddress() with
        | None ->
          do! startRPCListener cc
        | Some addr ->
          let zmqListener = ZmqBlockchainListener(opts, addr, loggerFactory, getBlockchainClient, cc, swapActor)
          match! zmqListener.CheckConnection ct with
          | true ->
            do! (zmqListener :> IHostedService).StartAsync(ct)
            match listeners.TryAdd(cc, zmqListener) with
            | true ->
              ()
            | false ->
              logger.LogError($"Failed to add {nameof(RPCLongPollingBlockchainListener)} ({cc})")
          | _ ->
            logger.LogWarning($"Failed to connect to zmq in {cc}. " +
                              "falling back to RPC long-polling. This might impact the performance")
            do! startRPCListener cc
      }

      do!
        opts.Value.OnChainCrypto
        |> Seq.map roundTrip
        |> Task.WhenAll
    }

    member this.StopAsync(cancellationToken) = unitTask {
      do! Task.WhenAll(listeners.Values |> Seq.cast<IHostedService> |> Seq.map(fun h -> h.StopAsync(cancellationToken)))
    }

  interface IBlockChainListener with
    member this.CurrentHeight cc = listeners.[cc].CurrentTip.Height

  interface ISwapEventListener with
    member this.RegisterSwap(swapId) =
      listeners |> Seq.iter(fun l -> (l.Value :> ISwapEventListener).RegisterSwap(swapId))
    member this.RemoveSwap(swapId) =
      listeners |> Seq.iter(fun l -> (l.Value :> ISwapEventListener).RemoveSwap(swapId))
