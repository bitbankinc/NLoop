namespace NLoop.Server

open System
open System.Linq
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Runtime.CompilerServices
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open FsToolkit.ErrorHandling
open FSharp.Control.Tasks
open LndClient
open NLoop.Domain

type ILightningClientProvider =
  abstract member TryGetClient: crypto: SupportedCryptoCode -> INLoopLightningClient option
  abstract member GetAllClients: unit -> INLoopLightningClient seq

[<AbstractClass;Sealed;Extension>]
type ILightningClientProviderExtensions =
  [<Extension>]
  static member GetClient(this: ILightningClientProvider, crypto: SupportedCryptoCode) =
    match this.TryGetClient crypto with
    | Some v -> v
    | None -> raise <| InvalidDataException($"cryptocode {crypto} is not supported for layer 2")

  [<Extension>]
  static member AsChangeAddressGetter(this: ILightningClientProvider) =
    NLoop.Domain.IO.GetAddress(fun c ->
      task {
        match this.TryGetClient(c) with
        | None -> return Error("Unsupported Cryptocode")
        | Some s ->
          let! c = s.GetDepositAddress()
          return (Ok(c :> IDestination))
      }
    )

type LightningClientProvider(logger: ILogger<LightningClientProvider> ,opts: IOptions<NLoopOptions>, httpClientFactory: IHttpClientFactory) =
  let clients = Dictionary<SupportedCryptoCode, INLoopLightningClient>()

  member private this.CheckClientConnection(c: SupportedCryptoCode) = task {
    let settings = opts.Value.GetLndGrpcSettings()
    let httpClient = httpClientFactory.CreateClient()
    httpClient.Timeout <- TimeSpan.FromDays(3.)
    let cli =
      NLoopLndGrpcClient(settings, opts.Value.GetNetwork(c), httpClient)
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

