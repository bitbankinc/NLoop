namespace NLoop.Server.Tests

open System
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open System.IO
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.CommandLine.Binding
open System.CommandLine.Builder
open System.CommandLine.Parsing

open BoltzClient
open FSharp.Control
open DotNetLightning.Payment
open DotNetLightning.Utils
open FsToolkit.ErrorHandling
open LndClient
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open NBitcoin.DataEncoders
open NBitcoin

open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost

open NBitcoin.RPC
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.SwapServerClient
open NLoop.Server.Projections


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
  let private checkConnection port =
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
      match checkConnection(int port) with
      | Ok _ ->
        ports.[i] <- port
        i <- i + 1
      | _ -> ()
    ports

  let findEmptyPort(ports: int[]) =
    findEmptyPortUInt(ports |> Array.map(uint))

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
  QueryRoutes: PubKey -> LNMoney -> Route
  GetInvoice: PaymentPreimage -> LNMoney -> TimeSpan -> string -> RouteHint[] -> PaymentRequest
  SubscribeSingleInvoice: PaymentHash -> AsyncSeq<InvoiceSubscription>
  GetChannelInfo: ShortChannelId -> GetChannelInfoResponse
}
  with
  static member Default = {
    ListChannels = []
    QueryRoutes = fun _ _ -> Route[]
    GetInvoice = fun preimage amount expiry memo hint ->
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
  LoopOutQuote: SwapDTO.LoopOutQuoteRequest -> Task<SwapDTO.LoopOutQuote>
  LoopInQuote: SwapDTO.LoopInQuoteRequest -> Task<SwapDTO.LoopInQuote>
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
      } |> Task.FromResult
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
  ListUnspent: unit -> UnspentCoin[]
  SignSwapTxPSBT: PSBT -> PSBT
  GetDepositAddress: unit -> BitcoinAddress
}
  with
  static member Default =
    {
      ListUnspent = fun () -> failwith "todo"
      SignSwapTxPSBT = fun _ -> failwith "todo"
      GetDepositAddress = fun () -> Helpers.walletAddress
    }

type TestHelpers =

  static member GetDummyLightningClient(?parameters) =
    let parameters = defaultArg parameters DummyLnClientParameters.Default
    {
      new INLoopLightningClient with
      member this.GetDepositAddress(?ct) =
        Helpers.lndAddress
        |> Task.FromResult
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
        parameters.GetInvoice paymentPreimage amount expiry memo routeHint
        |> Task.FromResult
      member this.Offer(req: SendPaymentRequest, ?ct: CancellationToken): Task<Result<PaymentResult, string>> =
        TaskResult.retn {
          PaymentPreimage = PaymentPreimage.Create(Array.zeroCreate 32)
          Fee = req.MaxFee.ToLNMoney()
        }

      member this.GetInfo(?ct: CancellationToken): Task<obj> =
        Task.FromResult(obj())

      member this.QueryRoutes(nodeId: PubKey, amount: LNMoney, ?ct: CancellationToken): Task<Route> =
        parameters.QueryRoutes nodeId amount
        |> Task.FromResult

      member this.OpenChannel(request: LndOpenChannelRequest, ?ct: CancellationToken): Task<Result<OutPoint, LndOpenChannelError>> =
        failwith "todo"
      member this.ConnectPeer(nodeId: PubKey, host: string, ?ct: CancellationToken): Task =
        Task.FromResult() :> Task
      member this.ListChannels(?ct: CancellationToken): Task<ListChannelResponse list> =
        Task.FromResult parameters.ListChannels
      member this.SubscribeChannelChange(?ct: CancellationToken): AsyncSeq<ChannelEventUpdate> =
        failwith "todo"
      member this.SubscribeSingleInvoice(invoiceHash: PaymentHash, ?ct: CancellationToken): AsyncSeq<InvoiceSubscription> =
        parameters.SubscribeSingleInvoice invoiceHash
      member this.GetChannelInfo(channelId: ShortChannelId, ?ct:CancellationToken): Task<GetChannelInfoResponse> =
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

        member this.GetLoopOutQuote(request: SwapDTO.LoopOutQuoteRequest, ?ct: CancellationToken): Task<SwapDTO.LoopOutQuote> =
          parameters.LoopOutQuote request

        member this.GetLoopInQuote(request: SwapDTO.LoopInQuoteRequest, ?ct: CancellationToken): Task<SwapDTO.LoopInQuote> =
          parameters.LoopInQuote request

        member this.GetLoopOutTerms(req, ?ct : CancellationToken): Task<SwapDTO.OutTermsResponse> =
          parameters.LoopOutTerms req
        member this.GetLoopInTerms(req, ?ct : CancellationToken): Task<SwapDTO.InTermsResponse> =
          parameters.LoopInTerms req
        member this.CheckConnection(?ct: CancellationToken): Task =
          failwith "todo"

        member this.ListenToSwapTx(swapId: SwapId, ?ct: CancellationToken): Task<Transaction> =
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
        member this.Execute(swapId, msg, source) =
          p.Execute(swapId, msg, source); Task.CompletedTask
        member this.GetAllEntities ct =
          failwith "todo"
    }

  static member GetDummyWalletClient(?parameters) =
    let p = defaultArg parameters DummyWalletClientParameters.Default
    {
      new IWalletClient with
        member this.ListUnspent() =
          p.ListUnspent() |> Task.FromResult
        member this.SignSwapTxPSBT(psbt) =
          p.SignSwapTxPSBT psbt |> Task.FromResult
        member this.GetDepositAddress() =
          p.GetDepositAddress() |> Task.FromResult
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
    let rc = NLoopServerCommandLine.getRootCommand()
    let p =
      CommandLineBuilder(rc)
        .UseMiddleware(Main.useWebHostMiddleware)
        .Build()
    services
      .AddSingleton<ISwapServerClient, BoltzSwapServerClient>()
      .AddHttpClient<BoltzClient>()
      .ConfigureHttpClient(fun _sp _client ->
        () // TODO: Inject Mock ?
        )
      |> ignore
    services
      .AddSingleton<BindingContext>(BindingContext(p.Parse(""))) // dummy for NLoop to not throw exception in `BindCommandLine`
      .AddSingleton<ILightningClientProvider>(TestHelpers.GetDummyLightningClientProvider())
      .AddSingleton<IFeeEstimator>(TestHelpers.GetDummyFeeEstimator())
      .AddSingleton<GetAllEvents<Swap.Event>>(Func<IServiceProvider, GetAllEvents<Swap.Event>>(fun _ _ct -> TaskResult.retn([])))
      .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
      .AddSingleton<IWalletClient>(TestHelpers.GetDummyWalletClient())
      .AddSingleton<GetNetwork>(Func<IServiceProvider, _>(fun sp (cc: SupportedCryptoCode) ->
        cc.ToNetworkSet().GetNetwork(Network.RegTest.ChainName)
      ))
      .AddSingleton<GetWalletClient>(Func<IServiceProvider,_>(fun sp cc ->
        sp.GetRequiredService<IWalletClient>()
      ))
      |> ignore
    configureServices |> Option.iter(fun conf -> conf services)

  static member GetTestServiceProvider(?configureServices) =
    let configureServices = defaultArg configureServices (fun _ -> ())
    let services = ServiceCollection()
    App.configureServicesTest services
    TestHelpers.ConfigureTestServices(services, configureServices)
    services.BuildServiceProvider()
  static member GetTestHost(?configureServices: IServiceCollection -> unit) =
    let configureServices = defaultArg configureServices (fun _ -> ())
    WebHostBuilder()
      .UseContentRoot(Directory.GetCurrentDirectory())
      .ConfigureAppConfiguration(fun configBuilder ->
        configBuilder.AddJsonFile("appsettings.test.json") |> ignore
        )
      .UseStartup<Helpers.TestStartup>()
      .ConfigureLogging(Main.configureLogging)
      .ConfigureTestServices(fun (services: IServiceCollection) ->
        TestHelpers.ConfigureTestServices(services, configureServices)
      )
      .UseTestServer()

