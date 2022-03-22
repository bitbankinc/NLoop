namespace NLoop.Server.Services

open System
open System.Collections.Concurrent
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open FSharp.Control.Tasks
open FsToolkit.ErrorHandling
open EventStore.ClientAPI.Transport.Http
open ExchangeSharp
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Server
open NLoop.Server

[<AbstractClass;Sealed;Extension>]
type ExchangeRateHelpers =
  [<Extension>]
  static member ToExchangeRate(this: ExchangeTicker): ExchangeRate =
    (this.Ask + this.Bid) / 2m

type ExchangeName = string
type ExchangeRateProvider(getOpts: GetOptions, logger: ILogger<ExchangeRateProvider>) =
  inherit BackgroundService()
  let exchangeRates = ConcurrentDictionary<PairId * ExchangeName, ExchangeRate>()
  let mutable _executingTask = null
  let mutable _stoppingCts = null

  let checkClientsSupported(ct: CancellationToken) = unitTask {
    let pairs =
      getOpts().PairIds
      |> Seq.toArray
      |> Array.filter(fun p -> p.Base <> p.Quote)
    let! tasks =
      getOpts().Exchanges
      |> Seq.map(fun exchange -> task {
        try
          try
            let pairStrings =
              pairs
              |> Array.map(fun p -> PairId.toString(&p))
            let! c = ExchangeAPI.GetExchangeAPIAsync(exchange)
            let! sock = c.GetTickersWebSocketAsync((fun responses ->
              for resp in responses do
                let p =
                  pairs
                  |> Seq.find(fun p ->
                    let struct(b, q) = p.Value
                    let k = resp.Key.Trim().ToLowerInvariant()
                    k.StartsWith(b.ToStringLowerInvariant()) &&
                      k.EndsWith(q.ToStringLowerInvariant())
                  )
                exchangeRates.AddOrUpdate((p, exchange), (resp.Value.ToExchangeRate()), (fun _p oldV -> resp.Value.ToExchangeRate()))
                |> ignore
              ()), pairStrings)

            sock.add_Connected(fun _sock -> logger.LogInformation($"socket for exchange {exchange} connected"); Task.CompletedTask)
            sock.add_Disconnected(fun _sock -> logger.LogWarning($"socket for exchange {exchange} disconnected"); Task.CompletedTask)
            return Some (Task.CompletedTask)
          with
          | :? ApplicationException as ex ->
            raise <| ex
            return failwith "unreachable"
          // fallback to long-polling
          | _ex ->
            let! c = ExchangeAPI.GetExchangeAPIAsync(exchange)
            let run() = unitTask {
              while not <| ct.IsCancellationRequested do
                logger.LogInformation($"getting rate from exchange {exchange}")
                for p in pairs do
                  let pairString = PairId.toString(&p)
                  let! ticker = c.GetTickerAsync(pairString)
                  exchangeRates.AddOrUpdate((p, exchange), (ticker.ToExchangeRate()), (fun _p oldV -> ticker.ToExchangeRate()))
                  |> ignore
                do! Task.Delay(TimeSpan.FromSeconds Constants.ExchangeLongPollingIntervalSec, ct)
            }
            return Some <| run()
        with
        | :? ApplicationException as e ->
          logger.LogError($"The exchange {exchange} is not supported in ExchangeSharp. (message: {e.Message})")
          return None
        | :? APIException as e ->
          logger.LogError($"Failed to subscribe to exchange {exchange}. This means either the exchange does not support the pairs we want " +
                          ", or there is a bug in ExchangeSharp. Or the exchange API itself is down. " +
                          $"We drop it from exchange-rate source. (stack trace: {e})")
          return None
        | :? NotImplementedException ->
          logger.LogError $"exchange {exchange} has no api for getting tickers. We drop it from exchange-rate source"
          return None
      })
      |> Task.WhenAll
      |> Task.map(Seq.choose id >> Seq.toArray)

    if tasks.Length = 0 then
      raise <| NLoopConfigException "No valid exchange provided."
    else
      return! Task.WhenAll(tasks)
  }

  member this.TryGetExchangeRate(pairId: PairId, _ct: CancellationToken) =
    let struct (b, q) = pairId.Value
    if b = q then Some(1m) else
    // Take the median value of all exchanges
    let rates =
      let ratesStraight =
        exchangeRates
        |> Seq.choose(fun kv ->
          let p, _exchangeName = kv.Key
          if p = pairId then
            Some kv.Value
          else
            None
          )
      let ratesReversed =
        exchangeRates
        |> Seq.choose(fun kv ->
          let p, _exchangeName = kv.Key
          if p.Reverse = pairId then
            Some (1m / kv.Value)
          else
            None
          )
      seq [ratesStraight; ratesReversed]
      |> Seq.concat
      |> Seq.toArray
    if rates.Length = 0 then None else
    let a = rates |> Array.sort
    a.[a.Length / 2]
    |> Some

  override this.ExecuteAsync(ct) =
    checkClientsSupported(ct)

  override this.StartAsync(_cancellationToken) =
    logger.LogInformation $"Starting exchange rate provider service..."
    base.StartAsync(_cancellationToken)

  override this.StopAsync(ct) =
    logger.LogInformation $"Stopping exchange rate provider service..."
    base.StopAsync(ct)
