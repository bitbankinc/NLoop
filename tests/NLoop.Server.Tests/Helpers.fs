namespace NLoop.Server.Tests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.IO
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.CommandLine.Binding
open System.CommandLine.Builder
open System.CommandLine.Parsing

open DotNetLightning.Chain
open FSharp.Control
open DotNetLightning.Payment
open DotNetLightning.Utils
open FsToolkit.ErrorHandling
open LndClient
open NBitcoin.Altcoins
open NBitcoin.DataEncoders
open NBitcoin
open NBitcoin.Crypto

open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost

open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.SwapServerClient
open NLoop.Server.Projections
open NLoop.Server.Services

module Helpers =
  let getLocalBoltzClient() =
    let httpClient =new  HttpClient()
    httpClient.BaseAddress <- Uri("http://localhost:9001")
    let b = BoltzClient(httpClient)
    b

  let private GetCertFingerPrint(filePath: string) =
    use cert = new X509Certificate2(filePath)
    use hashAlg = SHA256.Create()
    hashAlg.ComputeHash(cert.RawData)

  let hex = HexEncoder()
  let getCertFingerPrintHex (filePath: string) =
    GetCertFingerPrint filePath |> hex.EncodeData
  let private checkConnection(port) =
    let l = TcpListener(IPAddress.Loopback, port)
    try
      l.Start()
      l.Stop()
      Ok()
    with
    | :? SocketException -> Error("")

  let findEmptyPortUInt(ports: uint []) =
    let mutable i = 0
    while i < ports.Length do
      let mutable port = RandomUtils.GetUInt32() % 4000u
      port <- port + 10000u
      if (ports |> Seq.exists((=)port)) then () else
      match checkConnection((int)port) with
      | Ok _ ->
        ports.[i] <- port
        i <- i + 1
      | _ -> ()
    ports

  let findEmptyPort(ports: int[]) =
    findEmptyPortUInt(ports |> Array.map(uint))

  let dummyLnClient = {
    new INLoopLightningClient with
      member this.GetDepositAddress(?ct) =
        let k = new Key()
        Task.FromResult(k.PubKey.WitHash.GetAddress(Network.RegTest))
      member this.GetHodlInvoice(paymentHash: Primitives.PaymentHash,
                                 value: LNMoney,
                                 expiry: TimeSpan,
                                 routeHints: RouteHint[],
                                 memo: string,
                                 ?ct: CancellationToken) =
          Task.FromResult(failwith "todo")
      member this.GetInvoice(paymentPreimage: PaymentPreimage,
                             amount: LNMoney,
                             expiry: TimeSpan,
                             routeHint: RouteHint[],
                             memo: string,
                             ?ct: CancellationToken): Task<PaymentRequest> =
        let tags: TaggedFields = {
          Fields = [ TaggedField.DescriptionTaggedField(memo) ]
        }
        let deadline = DateTimeOffset.UtcNow + expiry
        PaymentRequest.TryCreate(Network.RegTest, amount |> Some, deadline, tags, (new Key()))
        |> ResultUtils.Result.deref
        |> Task.FromResult
      member this.Offer(req: SendPaymentRequest, ?ct: CancellationToken): Task<Result<PaymentResult, string>> =
        TaskResult.retn {
          PaymentPreimage = PaymentPreimage.Create(Array.zeroCreate 32)
          Fee = req.MaxFee.ToLNMoney()
        }

      member this.GetInfo(?ct: CancellationToken): Task<obj> =
        Task.FromResult(obj())

      member this.QueryRoutes(nodeId: PubKey, amount: LNMoney, ?ct: CancellationToken): Task<Route> =
        failwith "todo"
      member this.OpenChannel(request: LndOpenChannelRequest, ?ct: CancellationToken): Task<Result<OutPoint, LndOpenChannelError>> =
        failwith "todo"
      member this.ConnectPeer(nodeId: PubKey, host: string, ?ct: CancellationToken): Task =
        Task.FromResult() :> Task
      member this.ListChannels(?ct: CancellationToken): Task<ListChannelResponse list> =
        Task.FromResult []
      member this.SubscribeChannelChange(?ct: CancellationToken): AsyncSeq<ChannelEventUpdate> =
        failwith "todo"
      member this.SubscribeSingleInvoice(invoiceHash: PaymentHash, ?ct: CancellationToken): AsyncSeq<InvoiceSubscription> =
        failwith "todo"
      member this.GetChannelInfo(channelId: ShortChannelId, ?ct:CancellationToken): Task<GetChannelInfoResponse> =
        {
          Capacity = Money.Satoshis(10000m)
          Node1Policy = {
            Id = (new Key()).PubKey
            TimeLockDelta = BlockHeightOffset16(10us)
            MinHTLC = LNMoney.Satoshis(10)
            FeeBase = LNMoney.Satoshis(10)
            FeeProportionalMillionths = LNMoney.Satoshis(2)
            Disabled = false
          }
          Node2Policy = {
            Id = (new Key()).PubKey
            TimeLockDelta = BlockHeightOffset16(10us)
            MinHTLC = LNMoney.Satoshis(10)
            FeeBase = LNMoney.Satoshis(10)
            FeeProportionalMillionths = LNMoney.Satoshis(2)
            Disabled = false
          }
        }
        |> Task.FromResult
  }
  let getDummyLightningClientProvider() =
    { new ILightningClientProvider with
        member this.TryGetClient(cryptoCode) =
          dummyLnClient |> Some
        member this.GetAllClients() =
          seq [dummyLnClient]
        }

  let mockCheckpointDB = {
    new ICheckpointDB
      with
      member this.GetSwapStateCheckpoint(ct: CancellationToken): ValueTask<int64 voption> =
        ValueTask.FromResult(ValueNone)
      member this.SetSwapStateCheckpoint(checkpoint: int64, ct:CancellationToken): ValueTask =
        ValueTask()
  }

  let mockFeeEstimator = {
    new IFeeEstimator
      with
      member this.Estimate _target _cc =
        FeeRate(1000m)
        |> Task.FromResult
  }

  type TestStartup(env) =
    member this.Configure(appBuilder) =
      App.configureApp(appBuilder)

    member this.ConfigureServices(services) =
      App.configureServices true env services

type TestHelpers =
  static member GetTestHost(?configureServices: IServiceCollection -> unit) =
    WebHostBuilder()
      .UseContentRoot(Directory.GetCurrentDirectory())
      .ConfigureAppConfiguration(fun configBuilder ->
        configBuilder.AddJsonFile("appsettings.test.json") |> ignore
        )
      .UseStartup<Helpers.TestStartup>()
      .ConfigureLogging(Main.configureLogging)
      .ConfigureTestServices(fun (services: IServiceCollection) ->
        let rc = NLoopServerCommandLine.getRootCommand()
        let p =
          CommandLineBuilder(rc)
            .UseMiddleware(Main.useWebHostMiddleware)
            .Build()
        services
          .AddHttpClient<BoltzClient>()
          .ConfigureHttpClient(fun _sp _client ->
            () // TODO: Inject Mock ?
            )
          |> ignore
        services
          .AddSingleton<BindingContext>(BindingContext(p.Parse(""))) // dummy for NLoop to not throw exception in `BindCommandLine`
          .AddSingleton<ILightningClientProvider>(Helpers.getDummyLightningClientProvider())
          .AddSingleton<ICheckpointDB>(Helpers.mockCheckpointDB)
          .AddSingleton<IFeeEstimator>(Helpers.mockFeeEstimator)
          |> ignore

        configureServices |> Option.iter(fun c -> c services)
      )
      .UseTestServer()

