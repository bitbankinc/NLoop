namespace NLoop.Server.Tests

open Microsoft.AspNetCore.TestHost
open NLoop.Server.Services
open Xunit


type AutoLoopTests() =

  [<Fact>]
  member this.TestParameters() =
    use server = new TestServer(Helpers.getTestHost())
    let man = server.Services.GetService(typeof<AutoLoopManager>) :?> AutoLoopManager
    Assert.NotNull(man)
    ()

  [<Fact>]
  member this.TestValidateRestrictions() =
    ()

  [<Fact>]
  member this.RestrictedSuggestions() =
    ()

  [<Fact>]
  member this.TestSweepFeeLimit() =
    ()

  [<Fact>]
  member this.TestSuggestSwaps() =
    ()
