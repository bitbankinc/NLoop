module ServerIntegrationTests

open NLoop.Domain
open NLoop.Server.Tests.Extensions
open Xunit
open Xunit.Abstractions

let pairId = struct (SupportedCryptoCode.BTC, SupportedCryptoCode.LTC)

type ServerIntegrationTestsClass(_output: ITestOutputHelper) =

  [<Fact>]
  [<Trait("Docker", "Docker")>]
  member this.TestLndClientStreaming() =
    let cli = Clients.Create()
    ()
