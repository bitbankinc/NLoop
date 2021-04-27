namespace NLoop.Server.Tests.Extensions

open System
open System.Collections.Generic
open System.IO
open System.Linq
open System.Net.Http
open BTCPayServer.Lightning
open BTCPayServer.Lightning.CLightning
open BTCPayServer.Lightning.LND
open DockerComposeFixture
open Helpers
open NBitcoin
open NBitcoin.RPC
open NLoop.Server.Services

type Clients = {
  Bitcoin: RPCClient
  Litecoin: RPCClient
  User: {| Lnd: LndClient; |}
  Server: {| Lnd: LndClient; Boltz: BoltzClient |}
}

[<AutoOpen>]
module DockerFixtureExtensions =
  type DockerFixture with
    member this.StartFixture(testName: string) =
      let ports = Array.zeroCreate 5 |> findEmptyPort
      let env = Dictionary<string, obj>()
      env.Add("BITCOIND_RPC_PORT", ports.[0])
      env.Add("LITECOIND_RPC_PORT", ports.[1])
      env.Add("LND_USER_REST_PORT", ports.[2])
      env.Add("LND_SERVER_REST_PORT", ports.[3])
      env.Add("BOLTZ_PORT", ports.[4])
      let dataPath = Path.GetFullPath(testName)
      if (Directory.Exists(dataPath)) then
        Directory.Delete(dataPath, true)
      Directory.CreateDirectory(dataPath) |> ignore
      env.Add("DATA_PATH", dataPath)

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
        let lndMacaroonPath = Path.Join(dataPath, "lnd_user", "chain", "bitcoin", "regtest", "admin.macaroon")
        let lndCertThumbprint = getCertFingerPrintHex(Path.Join(dataPath, "lnd_user", "tls.cert"))
        LightningClientFactory.CreateClient($"type=lnd-rest;macaroonfilepath={lndMacaroonPath};certthumbprint={lndCertThumbprint};server=https://localhost:{ports.[2]}", Network.RegTest) :?> LndClient
      let serverLnd =
        let lndMacaroonPath = Path.Join(dataPath, "lnd_server", "chain", "bitcoin", "regtest", "admin.macaroon")
        let lndCertThumbprint = getCertFingerPrintHex(Path.Join(dataPath, "lnd_server", "tls.cert"))
        LightningClientFactory.CreateClient($"type=lnd-rest;macaroonfilepath={lndMacaroonPath};certthumbprint={lndCertThumbprint};server=https://localhost:{ports.[3]}", Network.RegTest) :?> LndClient
      let serverBoltz =
        let httpClient = new HttpClient()
        httpClient.BaseAddress <- Uri($"http://localhost:{ports.[4]}")
        BoltzClient(httpClient)
      { Clients.Bitcoin = bitcoinClient
        Litecoin = litecoinClient
        User = {| Lnd = userLnd |}
        Server = {| Lnd = serverLnd; Boltz = serverBoltz |} }
