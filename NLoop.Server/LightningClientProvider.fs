namespace NLoop.Server

open System
open System.IO
open System.Net.Http
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open BTCPayServer.Lightning.LND
open DotNetLightning.Utils.Primitives
open FSharp.Control.Tasks
open System.Collections.Generic
open BTCPayServer.Lightning
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.DataEncoders
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

  [<Extension>]
  static member AsChangeAddressGetter(this: ILightningClientProvider) =
    NLoop.Domain.IO.GetChangeAddress(fun c ->
      task {
        match this.TryGetClient(c) with
        | None -> return Error("Unsupported Cryptocode")
        | Some s ->
          let! c = s.GetDepositAddress()
          return (Ok(c :> IDestination))
      }
    )

  [<Extension>]
  static member AsDomainLNClient(this: ILightningClientProvider) =
    { new NLoop.Domain.IO.INLoopLightningClient with
        member __.Offer(cc, invoice) =
          match this.TryGetClient(cc) with
          | Some cli -> task {
              let! p = cli.Pay(invoice.ToString())
              match p.Result with
              | PayResult.Ok ->
                let hex = HexEncoder()
                let! p = (cli :?> LndClient).SwaggerClient.ListPaymentsAsync()
                let preimage =
                  p.Payments
                  |> Seq.filter(fun i -> i.Payment_hash = invoice.PaymentHash.Value.ToString())
                  |> Seq.exactlyOne
                  |> fun i -> i.Payment_preimage
                  |> hex.DecodeData
                  |> PaymentPreimage.Create
                return preimage
              | s ->
                return failwithf "Unexpected PayResult: %A (%s)" s p.ErrorDetail
            }
          | _ -> failwithf "Unreachable! Unexpected cryptoCode %A" cc
    }


type LightningClientProvider(opts: IOptions<NLoopOptions>) =
  let clients = Dictionary<SupportedCryptoCode, ILightningClient>()

  member this.CheckClientConnection(c, ct) = task {
    let n  = opts.Value.GetNetwork(c)
    let cli =
      let factory = LightningClientFactory(n)
      if (factory.HttpClient |> isNull |> not) then
        factory.HttpClient.Timeout <- TimeSpan.MaxValue
      let cli = factory.Create(opts.Value.ChainOptions.[c].LightningConnectionString)
      cli
    let! _info = cli.GetInfo(ct)
    clients.Add(c, cli)
    return ()
  }

  interface IHostedService with
    member this.StartAsync(ct) = unitTask {
      let! _ = Task.WhenAll([for c in opts.Value.OffChainCrypto -> this.CheckClientConnection(c, ct)])
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
