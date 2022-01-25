namespace NLoop.Server.Tests

open LndClient
open Xunit
open NBitcoin
open FSharp.Control.Tasks


type LightningClientTests() =

  [<Fact>]
  [<Trait("Docker", "On")>]
  member this.TestListeningInvoices() =
    let client = Helpers.userLndClient
    task {
      let! channels = client.ListChannels()
      ()
    }
