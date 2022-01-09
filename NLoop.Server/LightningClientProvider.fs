namespace NLoop.Server

open System
open System.Linq
open System.Collections.Generic
open System.Threading.Tasks
open System.Threading
open System.Net.Http
open System.Runtime.CompilerServices
open DotNetLightning.Utils
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open FsToolkit.ErrorHandling
open FSharp.Control.Tasks
open LndClient
open NLoop.Domain

type LightningClientProvider(logger: ILogger<LightningClientProvider> ,opts: IOptions<NLoopOptions>, httpClientFactory: IHttpClientFactory) =
  let clients = Dictionary<SupportedCryptoCode, INLoopLightningClient>()

  member private this.CheckClientConnection(c: SupportedCryptoCode) = task {
    let settings = opts.Value.GetLndGrpcSettings()
    let cli =
      NLoopLndGrpcClient(settings, opts.Value.GetNetwork(c))
      :> INLoopLightningClient
    clients.Add(c, cli)
    try
      let! _info = cli.GetInfo()
      ()
    with
    | ex ->
      logger.LogCritical($"Failed to connect to the LND for cryptocode: {c}. Check your settings are correct.")
      raise <| ex
  }

  interface IHostedService with
    member this.StartAsync(_ct) = unitTask {
      let! _ = Task.WhenAll([for c in opts.Value.OffChainCrypto -> this.CheckClientConnection(c)])
      ()
    }

    member this.StopAsync(_cancellationToken) = unitTask {
      return ()
    }

  interface ILightningClientProvider with
    member this.TryGetClient(crypto: SupportedCryptoCode) =
      match clients.TryGetValue(crypto) with
      | true, v -> v |> Some
      | _, _ -> None

    member this.GetAllClients() =
      clients.Values.AsEnumerable()

