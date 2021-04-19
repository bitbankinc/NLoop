namespace NLoop.Server.Services


open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open FSharp.Control
open System.Threading.Channels
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NLoop.Domain
open NLoop.Server
open NLoop.Server.Actors


type SwapEventListener(boltzClient: BoltzClient,
                       logger: ILogger<SwapEventListener>,
                       eventAggregator: EventAggregator,
                       network: Network,
                       actor: SwapActor
                       ) =
  inherit BackgroundService()

  override this.ExecuteAsync(stoppingToken) =
    unitTask {
        let mutable notComplete = true
        while notComplete do
          let! shouldContinue = boltzClient.SwapStatusChannel.Reader.WaitToReadAsync(stoppingToken)
          notComplete <- shouldContinue
          let! swapStatus = boltzClient.SwapStatusChannel.Reader.ReadAsync(stoppingToken)
          logger.LogInformation($"Swap {swapStatus.Id} status update: {swapStatus.NewStatus.SwapStatus}")
          do! this.HandleSwapUpdate(swapStatus)
    }
  member private this.HandleSwapUpdate(swapStatus) = unitTask {
    let cmd = { Swap.Data.SwapStatusUpdate.Response = swapStatus.NewStatus.ToDomain
                Swap.Data.SwapStatusUpdate.Id = swapStatus.Id
                Swap.Data.SwapStatusUpdate.Network = network }
    do! actor.Put(Swap.Command.SwapUpdate(cmd))
  }

type SwapEventListeners(boltzClientProvider: BoltzClientProvider,
                               opts: IOptions<NLoopOptions>,
                               loggerFactory: ILoggerFactory,
                               sp: IServiceProvider) =

  let d = Dictionary<string, SwapEventListener>()
  do
    for kv in opts.Value.ChainOptions do
      let n = opts.Value.GetNetwork(kv.Key)
      let listener =
        new SwapEventListener(boltzClientProvider.Invoke(n),
                              loggerFactory.CreateLogger(),
                              sp.GetRequiredService<EventAggregator>(),
                              n,
                              sp.GetRequiredService<SwapActor>()
                              )
      d.Add(kv.Key.ToString(), listener)


  member this.GetEventListener(cryptoCode: string) =
    match d.TryGetValue(cryptoCode) with
    | true, v -> v
    | false, _ -> raise <| NotSupportedException($"{cryptoCode}")

  member this.GetEventListener(cryptoCode: SupportedCryptoCode) =
    this.GetEventListener(cryptoCode.ToString())

  interface IHostedService with
    member this.StartAsync(token) = unitTask {
      for kv in d do
        do! kv.Value.StartAsync(token)
    }
    member this.StopAsync(token) = unitTask {
      for kv in d do
        do! kv.Value.StopAsync(token)
    }
