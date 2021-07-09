namespace NLoop.Server

open System
open System.IO
open System.Net.Http
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open BTCPayServer.Lightning.LND
open DotNetLightning.Payment
open DotNetLightning.Utils
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
    NLoop.Domain.IO.GetAddress(fun c ->
      task {
        match this.TryGetClient(c) with
        | None -> return Error("Unsupported Cryptocode")
        | Some s ->
          let! c = s.GetDepositAddress()
          return (Ok(c :> IDestination))
      }
    )

  [<Extension>]
  static member Offer(cli: ILightningClient, invoice: PaymentRequest, ct: CancellationToken) =
    task {
      let! p = cli.Pay(invoice.ToString(), ct).ConfigureAwait(false)
      match p.Result with
      | PayResult.Ok ->
        let hex = HexEncoder()
        let! p = (cli :?> LndClient).SwaggerClient.ListPaymentsAsync(ct).ConfigureAwait(false)
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

  [<Extension>]
  static member AddHodlInvoice(cli: ILightningClient, paymentHash: PaymentHash, amount: Money, expiry: BlockHeightOffset16, memo: string): Task<PaymentRequest> = task {
    let invoice: LightningInvoice =
      match cli with
      | :? LndClient as lndClient ->
        ()
      | _ ->
        raise <| NotSupportedException("Unknown LN client type")
      failwith "todo"
    return invoice.ToDNLInvoice()
  }

type LightningClientProvider(opts: IOptions<NLoopOptions>, httpClientFactory: IHttpClientFactory) =
  let clients = Dictionary<SupportedCryptoCode, ILightningClient>()

  member this.CheckClientConnection(c, ct) = task {
    let n  = opts.Value.GetNetwork(c)
    let cli =
      let factory = LightningClientFactory(n)
      if (factory.HttpClient |> isNull) then
        factory.HttpClient <- httpClientFactory.CreateClient()
      // We need this since `Pay` ing will hang in case of HODL invoice which is necessary for swapping.
      factory.HttpClient.Timeout <- TimeSpan.FromDays(3.)
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
