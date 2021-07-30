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
