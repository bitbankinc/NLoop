namespace NLoop.Server.Services

open System.IO
open System.Runtime.CompilerServices
open System.Threading.Tasks
open FSharp.Control.Tasks
open System.Collections.Generic
open BTCPayServer.Lightning
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Server

type ILightningClientProvider =
  abstract member TryGetClient: crypto: SupportedCryptoCode -> ILightningClient option


[<AbstractClass;Sealed;Extension>]
type ILightningClientProviderExtensions =
  [<Extension>]
  static member GetClient(this: ILightningClientProvider, crypto: SupportedCryptoCode) =
    match this.TryGetClient crypto with
    | Some v -> v
    | None -> raise <| InvalidDataException($"cryptocode {crypto} is not supported for layer 2")

type LightningClientProvider(opts: IOptions<NLoopOptions>) =
  let clients = Dictionary<SupportedCryptoCode, ILightningClient>()

  let startCompletion = TaskCompletionSource()
  member val StartCompletion = startCompletion.Task with get

  member this.CheckClientConnection(c) = task {
    let n  = opts.Value.GetNetwork(c)
    let cli = LightningClientFactory.CreateClient(opts.Value.ChainOptions.[c].LightningConnectionString, n)
    let! _info = cli.GetInfo()
    clients.Add(c, cli)
    return ()
  }

  interface IHostedService with
    member this.StartAsync(ct) = unitTask {
      let! _ = Task.WhenAll([for c in opts.Value.OffChainCrypto -> this.CheckClientConnection(c)])
      startCompletion.TrySetResult() |> ignore
      ()
    }

    member this.StopAsync(cancellationToken) = unitTask {
      return ()
    }

  interface ILightningClientProvider with
    member this.TryGetClient(crypto: SupportedCryptoCode) =
      match clients.TryGetValue(crypto) with
      | true, v -> v |> Some
      | _, _ -> None

