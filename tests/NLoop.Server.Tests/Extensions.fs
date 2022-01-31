namespace NLoop.Server.Tests

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
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
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
module internal ExtensionHelpers =
  let [<Literal>] walletName = "cashcow"
  let privKey1 = new Key(hex.DecodeData("0101010101010101010101010101010101010101010101010101010101010101"))
  let privKey2 = new Key(hex.DecodeData("0202020202020202020202020202020202020202020202020202020202020202"))
  let privKey3 = new Key(hex.DecodeData("0303030303030303030303030303030303030303030303030303030303030303"))
  let privKey4 = new Key(hex.DecodeData("0404040404040404040404040404040404040404040404040404040404040404"))
  let privKey5 = new Key(hex.DecodeData("0505050505050505050505050505050505050505050505050505050505050505"))
  let privKey6 = new Key(hex.DecodeData("0606060606060606060606060606060606060606060606060606060606060606"))
  let privKey7 = new Key(hex.DecodeData("0707070707070707070707070707070707070707070707070707070707070707"))
  let privKey8 = new Key(hex.DecodeData("0808080808080808080808080808080808080808080808080808080808080808"))
  let pubkey1 = privKey1.PubKey
  let pubkey2 = privKey2.PubKey
  let pubkey3 = privKey3.PubKey
  let pubkey4 = privKey4.PubKey
  let pubkey5 = privKey5.PubKey
  let pubkey6 = privKey6.PubKey
  let pubkey7 = privKey7.PubKey
  let pubkey8 = privKey8.PubKey

[<Struct>]
type ChannelBalanceRequirement = {
  MinimumOutgoing: LNMoney
  MinimumIncoming: LNMoney
}
  with
  member this.Sum = this.MinimumIncoming + this.MinimumOutgoing
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

  member this.AssureWalletIsReady(ct) = task {
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

  member this.AssureChannelIsOpen(requirement: ChannelBalanceRequirement, ct: CancellationToken) = task {
    let! channels = this.Server.BitcoinLnd.ListChannels(ct)
    let outgoingSum =
      channels |> List.sumBy(fun c -> c.LocalBalance.Satoshi)
    let incomingSum =
      channels |> List.sumBy(fun c -> c.RemoteBalance.Satoshi)

    if outgoingSum >= requirement.MinimumOutgoing.Satoshi && incomingSum >= requirement.MinimumIncoming.Satoshi then () else
    let mutable nodeId = null
    do! this.AssureWalletIsReady(ct)
    let! n = this.AssureConnected(ct)
    nodeId <- n

    let rec channelCreationLoop (count: int) = async {
      let req =
        { LndOpenChannelRequest.Private = None
          Amount = requirement.MinimumOutgoing + requirement.MinimumIncoming + LNMoney.Satoshis(10000) // buffer
          NodeId = nodeId
          CloseAddress = None }
      let! ct = Async.CancellationToken
      let! r = this.User.BitcoinLnd.OpenChannel(req, ct) |> Async.AwaitTask
      ct.ThrowIfCancellationRequested()
      match r with
      | Ok _fundingOutPoint ->
        let rec waitToSync count = async {
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
          return! channelCreationLoop(nextCount)
        else
          failwithf "Failed opening channel %A" e
    }
    let sum = outgoingSum + incomingSum
    if sum < requirement.Sum.Satoshi + 100L then
      let! _ =
        channelCreationLoop(0)
        |> fun a -> Async.StartAsTask(a, TaskCreationOptions.None, ct)
      ()
    else
      ()
    let! channels = this.User.BitcoinLnd.ListChannels(ct)
    let mutable outgoingSum =
      channels |> List.sumBy(fun c -> c.LocalBalance.Satoshi)
    let mutable incomingSum =
      channels |> List.sumBy(fun c -> c.RemoteBalance.Satoshi)
    let mutable channelIds = channels |> Seq.map(fun c -> c.Id)
    let sum = outgoingSum + incomingSum
    assert (sum >= requirement.Sum.Satoshi)
    let pay(origin: NLoopLndGrpcClient, dest: NLoopLndGrpcClient, amount: int64, channels) = task {
      let! inv = dest.GetInvoice(amount |> LNMoney.Satoshis)
      let req = {
        SendPaymentRequest.Invoice = inv
        MaxFee = Money.Satoshis(10000L)
        OutgoingChannelIds = channels |> Seq.toArray
        TimeoutSeconds = 10 }
      match! origin.SendPayment(req, ct) with
      | Error e -> failwith e
      | Ok _ -> ()
    }
    while not <| (outgoingSum >= requirement.MinimumOutgoing.Satoshi && incomingSum >= requirement.MinimumIncoming.Satoshi) do
      let needToPayIn = requirement.MinimumOutgoing.Satoshi - outgoingSum
      if 0L < needToPayIn then
        do! pay(this.Server.BitcoinLnd, this.User.BitcoinLnd, needToPayIn, channelIds)
      let needToPayOut = requirement.MinimumIncoming.Satoshi - incomingSum
      if 0L < needToPayOut then
        do! pay(this.User.BitcoinLnd, this.Server.BitcoinLnd, needToPayOut, channelIds)
      let! channels = this.User.BitcoinLnd.ListChannels(ct)
      outgoingSum <-
        channels |> List.sumBy(fun c -> c.LocalBalance.Satoshi)
      incomingSum <-
        channels |> List.sumBy(fun c -> c.Cap.Satoshi - c.LocalBalance.Satoshi)
      channelIds <- channels |> Seq.map(fun c -> c.Id)
      ()
    ()
  }
  member this.AssureChannelIsOpen(localBalance: LNMoney, ct: CancellationToken) =
    this.AssureChannelIsOpen({ MinimumOutgoing = localBalance; MinimumIncoming = LNMoney.Zero }, ct)
  static member GetExternalServiceClients() =
    let serverBoltz =
      let httpClient = new HttpClient()
      httpClient.BaseAddress <- Uri($"http://localhost:{boltzServerPort}")
      BoltzClient(httpClient)
    {
      ExternalClients.Bitcoin = RPCClient("johndoe:unsafepassword", Uri($"http://localhost:{bitcoinPort}"), Network.RegTest)
      Litecoin = RPCClient("johndoe:unsafepassword", Uri($"http://localhost:{litecoinPort}"), Litecoin.Instance.Regtest)
      User = {| BitcoinLnd = getUserLndClient() |}
      Server = {| BitcoinLnd = getServerBTCLndClient(); LitecoinLnd = getServerLTCLndClient(); Boltz = serverBoltz |}
    }

type Clients = {
  NLoopClient: NLoopClient
  ExternalClients: ExternalClients
  NLoopServer: TestServer
}
  with
  static member Create(?logServer: bool) =
    let externalClients = ExternalClients.GetExternalServiceClients()
    let logServer = defaultArg logServer false
    let testHost =
      WebHostBuilder()
        .UseContentRoot(dataPath)
        .UseStartup<Startup>()
        .ConfigureAppConfiguration(fun _b ->())
        .ConfigureLogging(Main.configureLogging)
        .ConfigureTestServices(fun s ->
          let cliOpts: ParseResult =
            let p =
              let rc = NLoopServerCommandLine.getRootCommand()
              CommandLineBuilder(rc)
                .UseMiddleware(Main.useWebHostMiddleware)
                .Build()
            let lndUri, lndCertThumbprint, lndMacaroonPath =
              getLndGrpcSettings lndUserPath lndUserGrpcPort
            p.Parse($"""--network regtest
                    --datadir {dataPath}
                    --nohttps true
                    --btc.rpcuser=johndoe
                    --btc.rpcpassword=unsafepassword
                    --btc.rpcport={bitcoinPort}
                    --ltc.rpcuser=johndoe
                    --ltc.rpcpassword=unsafepassword
                    --ltc.rpcport={litecoinPort}
                    --lndgrpcserver {lndUri}
                    --lndmacaroonfilepath {lndMacaroonPath}
                    --lndcertthumbprint {lndCertThumbprint}
                    --eventstoreurl tcp://admin:changeit@localhost:{esdbTcpPort}
                    --boltzhost http://localhost
                    --boltzport {esdbHttpPort}
                    --exchanges BitBank --exchanges BitMEX \
                    --boltzhttps false
                    """)
          s
            .AddSingleton<BindingContext>(BindingContext(cliOpts))
            .AddSingleton<BoltzClient>(externalClients.Server.Boltz)
          |> ignore
          if logServer then () else
            s.AddSingleton<ILoggerFactory, NullLoggerFactory>() |> ignore
        )
        |> fun b -> new TestServer(b)
    let userNLoop =
      let httpClient = testHost.CreateClient()
      let nloopClient = httpClient |> NLoopClient
      nloopClient.BaseUrl <- httpClient.BaseAddress.ToString()
      nloopClient
    {
      ExternalClients = externalClients
      NLoopClient = userNLoop
      NLoopServer = testHost
    }

  interface IDisposable with
    member this.Dispose() =
      this.NLoopServer.Dispose()
