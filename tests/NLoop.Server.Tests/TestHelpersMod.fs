namespace NLoop.Server.Tests

open System
open System.Collections.Generic
open System.CommandLine.Parsing
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open System.IO
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.CommandLine.Binding

open BoltzClient
open EventStore.ClientAPI
open FSharp.Control
open DotNetLightning.Payment
open DotNetLightning.Utils
open FsToolkit.ErrorHandling
open NLoopLnClient
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open NBitcoin.DataEncoders
open NBitcoin

open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost

open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.SwapServerClient

module TestHelpersMod =
  let getLocalBoltzClient() =
    let httpClient =new  HttpClient()
    httpClient.BaseAddress <- Uri("http://localhost:6028")
    let b = BoltzClient(httpClient)
    b

  let private GetCertFingerPrint(filePath: string) =
    use cert = new X509Certificate2(filePath)
    use hashAlg = SHA256.Create()
    hashAlg.ComputeHash(cert.RawData)

  let hex = HexEncoder()
  let getCertFingerPrintHex (filePath: string) =
    GetCertFingerPrint filePath |> hex.EncodeData

  let getLndGrpcSettings path port =
    let lndMacaroonPath = Path.Combine(path, "admin.macaroon")
    let lndCertThumbprint =
      getCertFingerPrintHex(Path.Combine(path, "tls.cert"))
    let uri = $"https://localhost:%d{port}"
    (uri, lndCertThumbprint, lndMacaroonPath)
  let getLightningClient path port network =
    let settings =
      let uri, tls, macaroonPath = getLndGrpcSettings path port
      LndGrpcSettings.Create(uri, None, Some macaroonPath, Some tls, true)
      |> function | Ok s -> s | Error e -> failwith e
    NLoopLndGrpcClient(settings, network)

  let [<Literal>] bitcoinPort = 43782
  let [<Literal>] litecoinPort = 43783
  let [<Literal>] lndUserGrpcPort = 32777
  let [<Literal>] lndServerGrpcPort_BTC = 32778
  let [<Literal>] lndServerGrpcPort_LTC = 32779
  let [<Literal>] boltzServerPort = 6028
  let [<Literal>] esdbTcpPort = 1113
  let [<Literal>] esdbHttpPort = 2113
  let dataPath =
    Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "data")

  let lndUserPath = Path.Combine(dataPath, "lnd_user")
  let getUserLndClient() =
    getLightningClient lndUserPath lndUserGrpcPort Network.RegTest
  let getServerBTCLndClient() =
    let path = Path.Combine(dataPath, "lnd_server_btc")
    getLightningClient path lndServerGrpcPort_BTC Network.RegTest
  let getServerLTCLndClient() =
    let path = Path.Combine(dataPath, "lnd_server_ltc")
    getLightningClient path lndServerGrpcPort_LTC NBitcoin.Altcoins.Litecoin.Instance.Regtest

  let [<Literal>] eventStoreUrl = "tcp://admin:changeit@localhost:1113"
  let getEventStoreDBConnection() =
    let connSettings = ConnectionSettings.Create().DisableTls().Build()
    let conn = EventStoreConnection.Create(connSettings, eventStoreUrl)
    do conn.ConnectAsync().GetAwaiter().GetResult()
    conn

  let walletAddress =
    new Key(hex.DecodeData("9898989898989898989898989898989898989898989898989898989898989898"))
    |> fun k -> k.PubKey.WitHash.GetAddress(Network.RegTest)

  let lndAddress =
    new Key(hex.DecodeData("9797979797979797979797979797979797979797979797979797979797979797"))
    |> fun k -> k.PubKey.WitHash.GetAddress(Network.RegTest)

  type TestStartup(env) =
    member this.Configure(appBuilder) =
      App.configureApp(appBuilder)

    member this.ConfigureServices(services) =
      App.configureServices true (Some env) services

type DummyLnClientParameters = {
  ListChannels: ListChannelResponse list
  QueryRoutes: PubKey -> LNMoney -> ShortChannelId option -> Route
  GetInvoice: PaymentPreimage -> LNMoney -> TimeSpan -> string -> RouteHint[] -> PaymentRequest
  SubscribeSingleInvoice: SubscribeSingleInvoiceRequest -> AsyncSeq<IncomingInvoiceSubscription>
  GetChannelInfo: ShortChannelId -> GetChannelInfoResponse
}
  with
  static member Default = {
    ListChannels = []
    QueryRoutes = fun _ _ _ -> Route[]
    GetInvoice = fun _preimage amount expiry memo _hint ->
      let tags: TaggedFields = {
        Fields = [ TaggedField.DescriptionTaggedField(memo) ]
      }
      let deadline = DateTimeOffset.UtcNow + expiry
      PaymentRequest.TryCreate(Network.RegTest, amount |> Some, deadline, tags, (new Key()))
      |> ResultUtils.Result.deref
    SubscribeSingleInvoice = fun _hash -> failwith "todo"
    GetChannelInfo = fun _cId -> failwith "todo"
  }

type DummySwapServerClientParameters = {
  LoopOutQuote: SwapDTO.LoopOutQuoteRequest -> Task<Result<SwapDTO.LoopOutQuote, string>>
  LoopInQuote: SwapDTO.LoopInQuoteRequest -> Task<Result<SwapDTO.LoopInQuote, string>>
  LoopOutTerms: SwapDTO.OutTermsRequest -> Task<SwapDTO.OutTermsResponse>
  LoopInTerms: SwapDTO.InTermsRequest -> Task<SwapDTO.InTermsResponse>
  GetNodes: unit -> SwapDTO.GetNodesResponse
  LoopOut: SwapDTO.LoopOutRequest -> Task<SwapDTO.LoopOutResponse>
  LoopIn: SwapDTO.LoopInRequest -> Task<SwapDTO.LoopInResponse>
}
  with
  static member Default = {
    LoopOutQuote = fun _ ->
      {
        SwapDTO.LoopOutQuote.SwapFee = Money.Satoshis(100L)
        SwapDTO.LoopOutQuote.SweepMinerFee = Money.Satoshis(10L)
        SwapDTO.LoopOutQuote.SwapPaymentDest = PubKey("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619")
        SwapDTO.LoopOutQuote.CltvDelta = BlockHeightOffset32(20u)
        SwapDTO.LoopOutQuote.PrepayAmount = Money.Satoshis(10L)
      } |> Ok |> Task.FromResult
    LoopInQuote = fun _ -> failwith "todo"
    LoopOutTerms = fun _ ->
      {
        SwapDTO.OutTermsResponse.MinSwapAmount = Money.Satoshis(1L)
        SwapDTO.OutTermsResponse.MaxSwapAmount = Money.Satoshis(10000L)
      }
      |> Task.FromResult
    LoopInTerms = fun _ ->
      {
        SwapDTO.InTermsResponse.MinSwapAmount = Money.Satoshis(1L)
        SwapDTO.InTermsResponse.MaxSwapAmount = Money.Satoshis(10000L)
      }
      |> Task.FromResult
    GetNodes = fun () -> {
      SwapDTO.GetNodesResponse.Nodes = Map.empty
    }
    LoopOut = fun _ -> failwith "todo"
    LoopIn = fun _ -> failwith "todo"
  }

type DummySwapActorParameters = {
  Execute: SwapId * Swap.Command * string option -> unit
}
  with
  static member Default = {
    Execute = fun (_, _, _) -> ()
  }

type DummyBlockChainClientParameters = {
  GetBlock: uint256 -> BlockWithHeight
  GetBlockHash: BlockHeight -> uint256
  GetBestBlockHash: unit -> uint256
  GetBlockchainInfo: unit -> BlockChainInfo
}
  with
  static member Default = {
    GetBlock = fun _hash -> failwith "todo"
    GetBlockHash = fun _height -> failwith "todo"
    GetBestBlockHash = fun () -> failwith "todo"
    GetBlockchainInfo = fun () -> failwith "todo"
  }

type DummyWalletClientParameters = {
  FundToAddress: BitcoinAddress * Money * BlockHeightOffset32 -> Task<uint256>
  GetDepositAddress: unit -> BitcoinAddress
  GetSendingTxFee:
    IDictionary<BitcoinAddress, Money> ->
      BlockHeightOffset32 ->
      Result<Money, WalletClientError>
}
  with
  static member Default =
    {
      FundToAddress = fun (_,_,_) -> failwith "todo"
      GetDepositAddress = fun () -> TestHelpersMod.walletAddress |> unbox
      GetSendingTxFee = fun _ _ -> failwith "todo"
    }

type TestHelpers =

  static member GetDummyLightningClient(?parameters) =
    let parameters = defaultArg parameters DummyLnClientParameters.Default
    {
      new INLoopLightningClient with
      member this.GetDepositAddress(?ct) =
        TestHelpersMod.lndAddress |> unbox
        |> Task.FromResult
      member this.GetInvoice(paymentPreimage: PaymentPreimage,
                             amount: LNMoney,
                             expiry: TimeSpan,
                             routeHint: RouteHint[],
                             memo: string,
                             ?ct: CancellationToken): Task<PaymentRequest> =
        parameters.GetInvoice paymentPreimage amount expiry memo routeHint
        |> Task.FromResult
      member this.SendPayment(req: SendPaymentRequest, ?ct: CancellationToken): Task<Result<PaymentResult, string>> =
        TaskResult.retn {
          PaymentPreimage = PaymentPreimage.Create(Array.zeroCreate 32)
          Fee = req.MaxFee.ToLNMoney()
        }

      member this.Offer(req, ?ct) =
        let offerResult = ()
        TaskResult.retn offerResult

      member this.GetInfo(?ct: CancellationToken): Task<obj> =
        Task.FromResult(obj())

      member this.QueryRoutes(nodeId: PubKey, amount: LNMoney, ?chanId: ShortChannelId, ?ct: CancellationToken): Task<Route> =
        parameters.QueryRoutes nodeId amount chanId
        |> Task.FromResult
      member this.ConnectPeer(nodeId: PubKey, host: string, ?ct: CancellationToken): Task =
        Task.FromResult() :> Task
      member this.ListChannels(?ct: CancellationToken): Task<ListChannelResponse list> =
        Task.FromResult parameters.ListChannels
      member this.TrackPayment(invoiceHash: PaymentHash, ?ct: CancellationToken): AsyncSeq<OutgoingInvoiceSubscription> =
        failwith "todo"
      member this.SubscribeSingleInvoice(req, ?ct: CancellationToken): AsyncSeq<IncomingInvoiceSubscription> =
        parameters.SubscribeSingleInvoice req
      member this.GetChannelInfo(channelId: ShortChannelId, ?ct:CancellationToken): Task<GetChannelInfoResponse option> =
        {
          Capacity = Money.Satoshis(10000m)
          Node1Policy = {
            Id = (new Key()).PubKey
            TimeLockDelta = BlockHeightOffset16(10us)
            MinHTLC = LNMoney.Satoshis(10)
            FeeBase = LNMoney.Satoshis(10)
            FeeProportionalMillionths = 2u
            Disabled = false
          }
          Node2Policy = {
            Id = (new Key()).PubKey
            TimeLockDelta = BlockHeightOffset16(10us)
            MinHTLC = LNMoney.Satoshis(10)
            FeeBase = LNMoney.Satoshis(10)
            FeeProportionalMillionths = 2u
            Disabled = false
          }
        }
        |> Some
        |> Task.FromResult
    }

  static member GetDummyLightningClientProvider(?parameters) =
    let parameters = defaultArg parameters DummyLnClientParameters.Default
    let dummyLnClient = TestHelpers.GetDummyLightningClient parameters
    {
      new ILightningClientProvider with
        member this.TryGetClient(cryptoCode) =
          dummyLnClient |> Some
        member this.GetAllClients() =
          seq [dummyLnClient]
        member this.Name = "TestClientProvider"
    }


  static member GetDummySwapServerClient(?parameters: DummySwapServerClientParameters) =
    let parameters = defaultArg parameters DummySwapServerClientParameters.Default
    {
      new ISwapServerClient with
        member this.LoopOut(request: SwapDTO.LoopOutRequest, ?ct: CancellationToken): Task<SwapDTO.LoopOutResponse> =
          parameters.LoopOut request
        member this.LoopIn(request: SwapDTO.LoopInRequest, ?ct: CancellationToken): Task<SwapDTO.LoopInResponse> =
          parameters.LoopIn request
        member this.GetNodes(?ct: CancellationToken): Task<SwapDTO.GetNodesResponse> =
          parameters.GetNodes()
          |> Task.FromResult

        member this.GetLoopOutQuote(request: SwapDTO.LoopOutQuoteRequest, ?ct: CancellationToken): Task<Result<SwapDTO.LoopOutQuote, string>> =
          parameters.LoopOutQuote request

        member this.GetLoopInQuote(request: SwapDTO.LoopInQuoteRequest, ?ct: CancellationToken): Task<Result<SwapDTO.LoopInQuote, string>> =
          parameters.LoopInQuote request

        member this.GetLoopOutTerms(req, ?ct : CancellationToken): Task<SwapDTO.OutTermsResponse> =
          parameters.LoopOutTerms req
        member this.GetLoopInTerms(req, ?ct : CancellationToken): Task<SwapDTO.InTermsResponse> =
          parameters.LoopInTerms req
        member this.CheckConnection(?ct: CancellationToken): Task =
          failwith "todo"
        member this.ListenToSwapTx(swapId: SwapId, onTx, ?ct: CancellationToken): Task =
          failwith "todo"
    }

  static member GetDummySwapActor(?parameters) =
    let p = defaultArg parameters DummySwapActorParameters.Default
    {
      new ISwapActor with
        member this.Handler =
            failwith "todo"
        member this.Aggregate =
          failwith "todo"
        member this.Execute(swapId, msg, source, commitError) =
          p.Execute(swapId, msg, source); Task.CompletedTask
        member this.GetAllEntities(_since, _ct) =
          failwith "todo"
    }

  static member GetDummyWalletClient(?parameters) =
    let p = defaultArg parameters DummyWalletClientParameters.Default
    {
      new IWalletClient with
        member this.FundToAddress(_, _, _,  _) =
          failwith "todo"

        member this.GetDepositAddress(_network, _ct) =
          p.GetDepositAddress() |> Task.FromResult

        member this.GetSendingTxFee(destinations, target, ct) =
          p.GetSendingTxFee destinations target
          |> Task.FromResult
    }

  static member GetDummySwapExecutor(?_parameters) =
    {
      new ISwapExecutor with
        member this.ExecNewLoopOut(req, height, source, ct) =
          failwith "todo"
        member this.ExecNewLoopIn(req, height, source, ct) =
          failwith "todo"
    }

  static member GetDummyBlockchainClient(?parameters) =
    let p = defaultArg parameters DummyBlockChainClientParameters.Default
    {
      new IBlockChainClient with
        member this.GetBlock(blockHash, _) =
          p.GetBlock blockHash
          |> Task.FromResult
        member this.GetBlockChainInfo _ =
          p.GetBlockchainInfo()
          |> Task.FromResult
        member this.GetBlockHash(height, _) =
          p.GetBlockHash height
          |> Task.FromResult
        member this.GetRawTransaction(txid, ct) =
          failwith "todo"
        member this.GetBestBlockHash(ct) =
          p.GetBestBlockHash()
          |> Task.FromResult

        member this.SendRawTransaction(tx, _) =
          Task.FromResult(tx.GetHash())

        member this.EstimateFee(_target, _) =
          FeeRate(1000m)
          |> Task.FromResult
    }

  static member GetDummyFeeEstimator() =
    {
      new IFeeEstimator
        with
        member this.Estimate _target _cc =
          FeeRate(1000m)
          |> Task.FromResult
    }

  static member private ConfigureTestServices(services: IServiceCollection, ?configureServices: IServiceCollection -> unit) =
    services
      .AddSingleton<ISwapServerClient, BoltzSwapServerClient>()
      .AddHttpClient<BoltzClient>()
      .ConfigureHttpClient(fun _sp _client ->
        () // TODO: Inject Mock ?
        )
      |> ignore
    let p = Parser()
    services
      .AddSingleton<BindingContext>(BindingContext(p.Parse(""))) // dummy for NLoop to not throw exception in `BindCommandLine`
      .AddSingleton<ILightningClientProvider>(TestHelpers.GetDummyLightningClientProvider())
      .AddSingleton<IFeeEstimator>(TestHelpers.GetDummyFeeEstimator())
      .AddSingleton<GetAllEvents<Swap.Event>>(Func<IServiceProvider, GetAllEvents<Swap.Event>>(fun _ _ _ct -> TaskResult.retn([])))
      .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
      .AddSingleton<IWalletClient>(TestHelpers.GetDummyWalletClient())
      .AddSingleton<GetNetwork>(Func<IServiceProvider, _>(fun sp (cc: SupportedCryptoCode) ->
        cc.ToNetworkSet().GetNetwork(Network.RegTest.ChainName)
      ))
      .AddSingleton<GetStore>(Func<IServiceProvider, _>(fun sp () ->
        NLoop.Domain.Utils.InMemoryStore.getEventStore()
      ))
      .AddSingleton<GetWalletClient>(Func<IServiceProvider,_>(fun sp _cc ->
        sp.GetRequiredService<IWalletClient>()
      ))
      |> ignore
    configureServices |> Option.iter(fun conf -> conf services)

  static member GetTestServiceProvider(?configureServices) =
    let configureServices = defaultArg configureServices (fun _ -> ())
    let services = ServiceCollection()
    App.configureServicesWithColdStart services
    TestHelpers.ConfigureTestServices(services, configureServices)
    services.BuildServiceProvider()

  static member GetTestHost(?configureServices: IServiceCollection -> unit) =
    let configureServices = defaultArg configureServices (fun _ -> ())
    WebHostBuilder()
      .UseContentRoot(Directory.GetCurrentDirectory())
      .ConfigureAppConfiguration(fun configBuilder ->
        configBuilder.AddJsonFile("appsettings.test.json") |> ignore
        )
      .UseStartup<TestHelpersMod.TestStartup>()
      .ConfigureTestServices(fun (services: IServiceCollection) ->
        TestHelpers.ConfigureTestServices(services, configureServices)
      )
      .UseTestServer()

  static member GetPluginTestHost(outStream, ?configureServices: IServiceCollection -> unit) =
    let configureServices = defaultArg configureServices (fun _ -> ())
    let hb =
      HostBuilder()
        .UseContentRoot(Directory.GetCurrentDirectory())
        .ConfigureAppConfiguration(fun configBuilder ->
          configBuilder.AddJsonFile("appsettings.test.json") |> ignore
          )
        .ConfigureWebHost(fun wb ->
          wb
            .ConfigureTestServices(fun (services: IServiceCollection) ->
              TestHelpers.ConfigureTestServices(services, configureServices)
            )
            .Configure(fun applicationBuilder -> ())
            .UseTestServer()
            |> ignore
        )
    hb.ConfigureAsPlugin(outStream, coldStart = true).StartAsync()
