namespace NLoop.Server.Tests

open System.Threading
open LndClient
open NLoop.Server.Tests.Extensions
open Xunit
open NBitcoin
open FSharp.Control.Tasks


type LightningClientTests() =

  [<Fact>]
  [<Trait("Docker", "On")>]
  member this.TestListeningInvoices() =
    let cli = ExternalClients.GetExternalServiceClients()
    task {
      use cts = new CancellationTokenSource()
      cts.CancelAfter(3000)
      let! channels = cli.User.BitcoinLnd.ListChannels(cts.Token)
      ()
    }
