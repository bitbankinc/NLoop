namespace NLoop.Server.Tests

open System.Threading
open LndClient
open Xunit
open NBitcoin
open FSharp.Control.Tasks


type LightningClientTests() =

  [<Fact>]
  [<Trait("Docker", "On")>]
  member this.TestListeningInvoices() =
    let client = TestHelpersMod.userLndClient
    task {
      use cts = new CancellationTokenSource()
      cts.CancelAfter(3000)
      let! channels = client.ListChannels(cts.Token)
      ()
    }
