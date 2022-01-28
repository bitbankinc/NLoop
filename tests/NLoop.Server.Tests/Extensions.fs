namespace NLoop.Server.Tests.Extensions

open System
open System.Collections.Generic
open System.CommandLine.Binding
open System.CommandLine.Builder
open System.CommandLine.Parsing

open System.IO
open System.Linq
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open BoltzClient
open FSharp.Control.Tasks

open DotNetLightning.Utils
open LndClient
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open NBitcoin
open NBitcoin.Altcoins
open NBitcoin.RPC
open NLoop.Server
open NLoop.Server.Services
open NLoop.Server.SwapServerClient
open NLoop.Server.Tests
open NLoop.Server.Tests.TestHelpersMod
open NLoopClient

[<AutoOpen>]
module private ExtensionHelpers =
  let getLndRestSettings path port =
    let lndMacaroonPath = Path.Join(path, "admin.macaroon")
    let lndCertThumbprint =
      getCertFingerPrintHex(Path.Join(path, "tls.cert"))
    let uri = $"https://localhost:%d{port}"
    (uri, lndCertThumbprint, lndMacaroonPath)
  let [<Literal>] bitcoinPort = 43782
  let [<Literal>] litecoinPort = 43783
  let [<Literal>] lndUserRestPort = 32736
  let [<Literal>] lndServerRestPort = 32737
  let [<Literal>] boltzServerPort = 6028
  let [<Literal>] esdbTcpPort = 1113
  let [<Literal>] esdbHttpPort = 2113
  let [<Literal>] walletName = "cashcow"
  let dataPath =
    Directory.GetCurrentDirectory()
    |> fun d -> Path.Join(d, "..", "..", "..", "data")
  let lndUserPath = Path.Join(dataPath, "lnd_user")

type ExternalClients = {
  Bitcoin: RPCClient
  Litecoin: RPCClient
  User: {| BitcoinLnd: NLoopLndGrpcClient; |}
  Server: {| BitcoinLnd: NLoopLndGrpcClient; LitecoinLnd: NLoopLndGrpcClient; Boltz: BoltzClient |}
}
  with
  member private this.AssureWalletIsCreated (ct) = task {
    let walletClient = this.Bitcoin.GetWallet(walletName)
    let mutable btcAddress = null
    try
      let! btcAddr = walletClient.GetNewAddressAsync()
      btcAddress <- btcAddr
    with
    | :? RPCException as ex when ex.Message = "Requested wallet does not exist or is not loaded" ->
      let! _ = walletClient.CreateWalletAsync(walletName)
      let! btcAddr = walletClient.GetNewAddressAsync()
      btcAddress <- btcAddr
      ()
    return (walletClient, btcAddress)
  }

  member private this.AssureWalletHasEnoughBalance(ct: CancellationToken) = task {
      let! walletClient, btcAddress = this.AssureWalletIsCreated ct
      let! balance = walletClient.GetBalanceAsync()
      if balance < Money.Coins(21m) then
        let! _ = walletClient.GenerateToAddressAsync(Network.RegTest.Consensus.CoinbaseMaturity + 1, btcAddress)
        ()
      else
        ()
      ct.ThrowIfCancellationRequested()
      let! litecoinBalance = this.Litecoin.GetBalanceAsync()
      let! ltcAddress =
        let req = GetNewAddressRequest()
        req.AddressType <- AddressType.Legacy
        this.Litecoin.GetNewAddressAsync(req)
      if litecoinBalance < Money.Coins(21m) then
        let! _ = this.Litecoin.GenerateToAddressAsync(Litecoin.Instance.Regtest.Consensus.CoinbaseMaturity + 1, ltcAddress)
        ()
      else
        ()
      ct.ThrowIfCancellationRequested()
      return (walletClient, btcAddress, ltcAddress)
  }

  member private this.AssureWalletIsReady(ct) = task {
    let! walletClient, btcAddress, ltcAddress = this.AssureWalletHasEnoughBalance(ct)

    let assureLndHasEnoughCash (cli: NLoopLndGrpcClient) = task {
      let balance =
        let req = Lnrpc.WalletBalanceRequest()
        cli.Client.WalletBalance(req, cli.DefaultHeaders, cli.Deadline, ct)
      let required = Money.Coins(10m)
      if balance.TotalBalance >= required.Satoshi then () else
      let! addr = cli.GetDepositAddress(ct)
      let! _ = walletClient.SendToAddressAsync(addr, required)
      return ()
    }
    let assureLndHasEnoughCash_LTC (cli: NLoopLndGrpcClient) = task {
      let balance =
        let req = Lnrpc.WalletBalanceRequest()
        cli.Client.WalletBalance(req, cli.DefaultHeaders, cli.Deadline, ct)
      let required = Money.Coins(10m)
      if balance.TotalBalance >= required.Satoshi then () else
      let! addr = cli.GetDepositAddress(ct)
      let! _ = this.Litecoin.SendToAddressAsync(addr, required)
      return ()
    }

    let! _ =
      [| assureLndHasEnoughCash this.User.BitcoinLnd; assureLndHasEnoughCash this.Server.BitcoinLnd; assureLndHasEnoughCash_LTC this.Server.LitecoinLnd |]
      |> Task.WhenAll

    // confirm
    let! _ = walletClient.GenerateToAddressAsync(3, btcAddress)
    let! _ = this.Litecoin.GenerateToAddressAsync(3, ltcAddress)
    ()
  }
  member this.AssureConnected(ct) = task {
    let! nodes = this.Server.Boltz.GetNodesAsync(ct)
    let connString =
      nodes.Nodes |> Map.toSeq |> Seq.head |> fun (_, info) -> info.Uris.[0]
    try
      do! this.User.BitcoinLnd.ConnectPeer(connString.NodeId, connString.EndPoint.ToEndpointString(), ct)
    with
    | :? Grpc.Core.RpcException as ex when ex.Message.Contains("already connected to peer") ->
      ()
    return connString.NodeId
  }

  member this.AssureChannelIsOpen(localBalance: LNMoney, ct: CancellationToken) = task {
    let! channels = this.Server.BitcoinLnd.ListChannels(ct)
    if channels |> Seq.exists(fun c -> c.LocalBalance.Satoshi > localBalance.Satoshi) then () else

    let mutable nodeId = null
    do! this.AssureWalletIsReady(ct)
    let! n = this.AssureConnected(ct)
    nodeId <- n

    let rec loop (count: int) = async {
      let req =
        { LndOpenChannelRequest.Private = None
          Amount = localBalance
          NodeId = nodeId
          CloseAddress = None }
      let! r = this.User.BitcoinLnd.OpenChannel(req, ct) |> Async.AwaitTask
      let! ct = Async.CancellationToken
      ct.ThrowIfCancellationRequested()
      match r with
      | Ok _fundingOutPoint ->
        let rec waitToSync(count) = async {
          do! Async.Sleep(500)
          let! ct = Async.CancellationToken
          ct.ThrowIfCancellationRequested()
          let! btcAddr = this.Bitcoin.GetNewAddressAsync() |> Async.AwaitTask
          let! _ = this.Bitcoin.GenerateToAddressAsync(2, btcAddr) |> Async.AwaitTask
          let! s = this.User.BitcoinLnd.ListChannels(ct) |> Async.AwaitTask
          ct.ThrowIfCancellationRequested()
          match s with
          | [] ->
            if count > 4 then failwith "Failed to Create Channel" else
            return! waitToSync(count + 1)
          | _s ->
            return ()
        }
        do! waitToSync(0)
      | Error e ->
        if (count <= 3 && e.StatusCode.IsSome && e.StatusCode.Value >= 500) then
          let nextCount = count + 1
          do! Async.Sleep(1000 *  nextCount)
          printfn "retrying channel open..."
          return! loop(nextCount)
        else
          failwithf "Failed opening channel %A" e
    }
    return!
      loop(0)
      |> fun a -> Async.StartAsTask(a, TaskCreationOptions.None, ct)
  }
  static member GetExternalServiceClients() =
    let serverBoltz =
      let httpClient = new HttpClient()
      httpClient.BaseAddress <- Uri($"http://localhost:{boltzServerPort}")
      BoltzClient(httpClient)
    {
      ExternalClients.Bitcoin = RPCClient("johndoe:unsafepassword", Uri($"http://localhost:{bitcoinPort}"), Network.RegTest)
      Litecoin = RPCClient("johndoe:unsafepassword", Uri($"http://localhost:{litecoinPort}"), Network.RegTest)
      User = {| BitcoinLnd = userLndClient() |}
      Server = {| BitcoinLnd = serverBTCLndClient(); LitecoinLnd = serverLTCLndClient(); Boltz = serverBoltz |}
    }

type Clients = {
  NLoopClient: NLoopClient
  External: ExternalClients
  NLoopServer: TestServer
}
  with
  static member Create() =
    let externalClients = ExternalClients.GetExternalServiceClients()
    let testHost =
      WebHostBuilder()
        .UseContentRoot(dataPath)
        .UseStartup<TestStartup>()
        .ConfigureAppConfiguration(fun _b ->())
        .ConfigureLogging(Main.configureLogging)
        .ConfigureTestServices(fun s ->
          let lnClientProvider =
            { new ILightningClientProvider with
                member this.TryGetClient(cryptoCode) =
                  externalClients.User.BitcoinLnd :> INLoopLightningClient |> Some
                member this.GetAllClients() =
                  seq [externalClients.User.BitcoinLnd]
            }
          let cliOpts: ParseResult =
            let p =
              let rc = NLoopServerCommandLine.getRootCommand()
              CommandLineBuilder(rc)
                .UseMiddleware(Main.useWebHostMiddleware)
                .Build()
            let uri, lndCertThumbprint, lndMacaroonPath =
              getLndRestSettings lndUserPath lndUserRestPort
            p.Parse($"""--network RegTest
                    --datadir {dataPath}
                    --nohttps true
                    --btc.rpcuser=johndoe
                    --btc.rpcpassword=unsafepassword
                    --btc.rpcport={bitcoinPort}
                    --ltc.rpcuser=johndoe
                    --ltc.rpcpassword=unsafepassword
                    --ltc.rpcport={litecoinPort}
                    --lndserver {uri}
                    --lndmacaroonfilepath {lndMacaroonPath}
                    --lndcertthumbprint {lndCertThumbprint}
                    --eventstoreurl tcp://admin:changeit@localhost:{esdbTcpPort}
                    --boltzhost http://localhost
                    --boltzport {esdbHttpPort}
                    --boltzhttps false
                    """)
          s
            .AddSingleton<BindingContext>(BindingContext(cliOpts))
            .AddSingleton<ILightningClientProvider>(lnClientProvider)
            .AddSingleton<BoltzClient>(externalClients.Server.Boltz)
          |> ignore
        )
        |> fun b -> new TestServer(b)
    let userNLoop =
      let httpClient = testHost.CreateClient()
      let nloopClient = httpClient |> NLoopClient
      nloopClient.BaseUrl <- httpClient.BaseAddress.ToString()
      nloopClient
    {
      External = externalClients
      NLoopClient = userNLoop
      NLoopServer = testHost
    }
