namespace NLoop.Server.Tests.Extensions

open System
open System.Collections.Generic
open System.CommandLine.Binding
open System.CommandLine.Builder
open System.CommandLine.Parsing

open System.IO
open System.Linq
open System.Net.Http
open BoltzClient
open FSharp.Control.Tasks

open DotNetLightning.Utils
open LndClient
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open NBitcoin
open NBitcoin.RPC
open NLoop.Server
open NLoop.Server.Services
open NLoop.Server.SwapServerClient
open NLoop.Server.Tests.Helpers
open NLoopClient

[<AutoOpen>]
module private Helpers =
  let getLndRestSettings(path) port =
    let lndMacaroonPath = Path.Join(path, "chain", "bitcoin", "regtest", "admin.macaroon")
    let lndCertThumbprint =
      getCertFingerPrintHex(Path.Join(path, "tls.cert"))
    let uri = $"https://localhost:%d{port}"
    (uri, lndCertThumbprint, lndMacaroonPath)
  let getLNDClient (path) port  =
    let (uri, lndCertThumbprint, lndMacaroonPath) = getLndRestSettings path port
    let settings =
      LndGrpcSettings.Create(uri, lndCertThumbprint |> Some, None, Some <| lndMacaroonPath, false)
      |> function | Ok x -> x | Error e -> failwith e
    NLoopLndGrpcClient(settings, Network.RegTest)

type Clients = {
  Bitcoin: RPCClient
  Litecoin: RPCClient
  User: {| Lnd: INLoopLightningClient; NLoop: NLoopClient; NLoopServer: TestServer |}
  Server: {| Lnd: INLoopLightningClient; Boltz: BoltzClient |}
}
  with
  member this.AssureWalletIsReady() = task {
    let! btcAddr = this.Bitcoin.GetNewAddressAsync()
    let! _ = this.Bitcoin.GenerateToAddressAsync(Network.RegTest.Consensus.CoinbaseMaturity + 1, btcAddr)

    let send (cli: INLoopLightningClient) = task {
      let! addr = cli.GetDepositAddress()
      let! _ = this.Bitcoin.SendToAddressAsync(addr, Money.Coins(10m))
      return ()
    }
    do! send (this.User.Lnd)
    do! send (this.Server.Lnd)

    let! _ = this.Bitcoin.GenerateToAddressAsync(3, btcAddr)
    ()
  }

  member this.AssureConnected() = task {
    let! nodes = this.Server.Boltz.GetNodesAsync()
    let connString =
      nodes.Nodes |> Map.toSeq |> Seq.head |> fun (_, info) -> info.Uris.[0]
    do! this.User.Lnd.ConnectPeer(connString.NodeId, connString.EndPoint.ToEndpointString())
    return connString.NodeId
  }

  member this.OpenChannel(amount: LNMoney) =
    let mutable nodeId = null
    task {
      do! this.AssureWalletIsReady()
      let! n = this.AssureConnected()
      nodeId <- n
    } |> fun t -> t.GetAwaiter().GetResult()

    let rec loop (count: int) = async {
      let req =
        { LndOpenChannelRequest.Private = None
          Amount = amount
          NodeId = nodeId
          CloseAddress = None }
      let! r = this.User.Lnd.OpenChannel(req) |> Async.AwaitTask
      match r with
      | Ok _fundingOutPoint ->
        let rec waitToSync(count) = async {
          do! Async.Sleep(500)
          let! btcAddr = this.Bitcoin.GetNewAddressAsync() |> Async.AwaitTask
          let! _ = this.Bitcoin.GenerateToAddressAsync(2, btcAddr) |> Async.AwaitTask
          let! s = this.Server.Lnd.ListChannels() |> Async.AwaitTask
          match s with
          | [] ->
            if count > 4 then failwith "Failed to Create Channel" else
            return! waitToSync(count + 1)
          | s ->
            printfn $"\n\nSuccessfully created a channel with cap: {s.First().Cap.Satoshi}. balance: {s.First().LocalBalance.Satoshi}\n\n"
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
    loop(0)

  static member Create() =
    let bitcoinPort = 43782
    let litecoinPort = 43783
    let lndUserRestPort = 32736
    let lndServerRestPort = 32737
    let boltzServerPort = 6028
    let esdbTcpPort = 1113
    let esdbHttpPort = 2113

    let dataPath =
      Directory.GetCurrentDirectory()
      |> fun d -> Path.Join(d, "..", "..", "..", "data")
    let lndUserPath = Path.Join(dataPath, "lnd_user")
    let userLnd = getLNDClient lndUserPath lndUserRestPort
    let serverLnd = getLNDClient(Path.Join(dataPath, "lnd_server")) lndServerRestPort
    let serverBoltz =
      let httpClient = new HttpClient()
      httpClient.BaseAddress <- Uri($"http://localhsot:{boltzServerPort}")
      BoltzClient(httpClient)
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
                  userLnd :> INLoopLightningClient |> Some
                member this.GetAllClients() =
                  seq [userLnd]
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
            .AddSingleton<BoltzClient>(serverBoltz)
          |> ignore
        )
        |> fun b -> new TestServer(b)
    let userNLoop =
      let httpClient = testHost.CreateClient()
      let nloopClient = httpClient |> NLoopClient
      nloopClient.BaseUrl <- httpClient.BaseAddress.ToString()
      nloopClient
    {
      Bitcoin = RPCClient("johndoe:unsafepassword", Uri($"http://localhost:{bitcoinPort}"), Network.RegTest)
      Litecoin = RPCClient("johndoe:unsafepassword", Uri($"http://localhost:{litecoinPort}"), Network.RegTest)
      User = {| Lnd = userLnd; NLoop = userNLoop; NLoopServer = testHost |}
      Server = {| Lnd = serverLnd; Boltz= serverBoltz |}
    }
