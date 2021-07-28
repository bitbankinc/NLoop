namespace NLoop.Server.Tests.Extensions

open System
open System.Collections.Generic
open System.CommandLine.Binding
open System.CommandLine.Builder
open System.CommandLine.Parsing

open System.IO
open System.Linq
open System.Net.Http
open FSharp.Control.Tasks

open DockerComposeFixture
open DotNetLightning.Utils
open LndClient
open Helpers
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open NBitcoin
open NBitcoin.RPC
open NLoop.Server
open NLoop.Server.Services
open NLoopClient

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
      | Ok () ->
        do! Async.Sleep(500)
        let! btcAddr = this.Bitcoin.GetNewAddressAsync() |> Async.AwaitTask
        let! _ = this.Bitcoin.GenerateToAddressAsync(3, btcAddr) |> Async.AwaitTask
        let! s = this.Server.Lnd.GetInfo() |> Async.AwaitTask
        return ()
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

[<AutoOpen>]
module DockerFixtureExtensions =
  let private getLndRestSettings(path) port =
    let lndMacaroonPath = Path.Join(path, "chain", "bitcoin", "regtest", "admin.macaroon")
    let lndCertThumbprint =
      getCertFingerPrintHex(Path.Join(path, "tls.cert"))
    let uri = $"https://localhost:%d{port}"
    (uri, lndCertThumbprint, lndMacaroonPath)
  let private getLNDClient (path) port  =
    let (uri, lndCertThumbprint, lndMacaroonPath) = getLndRestSettings path port
    let settings =
      LndRestSettings.Create(uri, lndCertThumbprint |> Some, None, Some <| lndMacaroonPath, false)
      |> function | Ok x -> x | Error e -> failwith e
    LndNSwagClient(Network.RegTest, settings)

  type DockerFixture with
    member this.StartFixture(testName: string) =
      let ports = Array.zeroCreate 7 |> findEmptyPort
      let env = Dictionary<string, obj>()
      env.Add("BITCOIND_RPC_PORT", ports.[0])
      env.Add("LITECOIND_RPC_PORT", ports.[1])
      env.Add("LND_USER_REST_PORT", ports.[2])
      env.Add("LND_SERVER_REST_PORT", ports.[3])
      env.Add("BOLTZ_PORT", ports.[4])
      env.Add("ESDB_TCP_PORT", ports.[5])
      env.Add("ESDB_HTTP_PORT", ports.[6])
      let dataPath = Path.GetFullPath(testName)
      if (Directory.Exists(dataPath)) then
        Directory.Delete(dataPath, true)
      Directory.CreateDirectory(dataPath) |> ignore
      env.Add("DATA_PATH", dataPath)

      Directory.CreateDirectory(Path.Join(dataPath, "bitcoind")) |> ignore

      let boltzDir = Path.Join(dataPath, "boltz")
      Directory.CreateDirectory(boltzDir) |> ignore
      let oldFile = Path.Join(dataPath, "..", "data", "boltz", "boltz.conf")
      let newFile = Path.Join(dataPath, "boltz", "boltz.conf")
      File.Copy(oldFile, newFile)
      let oldFile = Path.Join(dataPath, "..", "data", "boltz", "bitcoind.cookie")
      let newFile = Path.Join(dataPath, "boltz", "bitcoind.cookie")
      File.Copy(oldFile, newFile)

      this.InitAsync(fun () ->
        let opts = DockerFixtureOptions() :> IDockerFixtureOptions
        opts.DockerComposeFiles <- [| "docker-compose.yml" |]
        opts.EnvironmentVariables <- env
        opts.DockerComposeDownArgs <- "--remove-orphans --volumes"
        // we need this because c-lightning is not working well with bind mount.
        // If we use volume mount instead, this is the only way to recreate the volume at runtime.
        opts.DockerComposeUpArgs <- "--renew-anon-volumes"
        opts.StartupTimeoutSecs <- 200
        opts.CustomUpTest <- fun o ->
          o.Any(fun x -> x.Contains "API server listening on:") // boltz
          && o.Count(fun x -> x.Contains "BTCN: Server listening on") = 2 // lnd
        opts
        ).GetAwaiter().GetResult()

      let bitcoinClient = RPCClient("johndoe:unsafepassword", Uri($"http://localhost:{ports.[0]}"), Network.RegTest)
      let litecoinClient = RPCClient("johndoe:unsafepassword", Uri($"http://localhost:{ports.[1]}"), Network.RegTest)
      let userLnd =
        getLNDClient(Path.Join(dataPath, "lnd_user")) ports.[2]
      let serverLnd =
        getLNDClient(Path.Join(dataPath, "lnd_server")) ports.[3]
      let serverBoltz =
        let httpClient = new HttpClient()
        httpClient.BaseAddress <- Uri($"http://localhost:{ports.[4]}")
        BoltzClient(httpClient)
      let testHostForDocker =
        let dataPath = Path.GetFullPath(testName)
        WebHostBuilder()
          .UseContentRoot(dataPath)
          .UseStartup<TestStartup>()
          .ConfigureAppConfiguration(fun builder ->
            ()
          )
          .ConfigureLogging(Main.configureLogging)
          .ConfigureTestServices(fun (services: IServiceCollection) ->
            let lnClientProvider =
              { new ILightningClientProvider with
                member this.TryGetClient(cryptoCode) =
                  userLnd
                  :> INLoopLightningClient
                  |> Some
             }
            let cliOpts =
              let p =
                let rc = NLoopServerCommandLine.getRootCommand()
                CommandLineBuilder(rc)
                  .UseMiddleware(Main.useWebHostMiddleware)
                  .Build()
              let (uri, lndCertThumbprint, lndMacaroonPath) = getLndRestSettings (Path.Join(dataPath, "lnd_user")) ports.[2]
              p.Parse($"""--network RegTest
                      --datadir {dataPath}
                      --nohttps true
                      --btc.rpcuser=johndoe
                      --btc.rpcpassword=unsafepassword
                      --btc.rpcport={ports.[0]}
                      --ltc.rpcuser=johndoe
                      --ltc.rpcpassword=unsafepassword
                      --ltc.rpcport={ports.[1]}
                      --lndserver {uri}
                      --lndmacaroonfilepath {lndMacaroonPath}
                      --lndcertthumbprint {lndCertThumbprint}
                      --eventstoreurl tcp://admin:changeit@localhost:{ports.[5]}
                      --boltzhost http://localhost
                      --boltzport {ports.[4]}
                      --boltzhttps false
                      """)
            services
              .AddSingleton<BindingContext>(BindingContext(cliOpts))
              .AddSingleton<ILightningClientProvider>(lnClientProvider)
              .AddSingleton<BoltzClient>(serverBoltz)
              .AddSingleton<IRepositoryProvider>(Helpers.getTestRepositoryProvider())
              |> ignore
          )
          |> fun b -> new TestServer(b)
      let userNLoop =
        let httpClient = testHostForDocker.CreateClient()
        let nloopClient = httpClient |> NLoopClient
        nloopClient.BaseUrl <- httpClient.BaseAddress.ToString()
        nloopClient
      { Clients.Bitcoin = bitcoinClient
        Litecoin = litecoinClient
        User = {| Lnd = userLnd; NLoop = userNLoop; NLoopServer = testHostForDocker |}
        Server = {| Lnd = serverLnd; Boltz = serverBoltz |} }

  open Microsoft.AspNetCore.TestHost
