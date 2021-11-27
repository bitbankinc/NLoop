namespace NLoop.Server.Tests

open System
open FSharp.Control
open System.Threading
open DotNetLightning.Payment
open DotNetLightning.Utils
open DotNetLightning.Utils.Primitives
open FSharp.Control.Tasks
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open LndClient
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open NBitcoin
open NBitcoin.RPC
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.Services
open NLoop.Server.Tests.Extensions
open Xunit

type AutoLoopTests() =

  let defaultTestRestrictions = {
    Restrictions.Minimum = Money.Satoshis 1L
    Maximum = Money.Satoshis 10000L
  }
  [<Fact>]
  member this.TestParameters() = task {
    use server = new TestServer(TestHelpers.GetTestHost(fun services ->
      services
        |> ignore
    ))
    let man = server.Services.GetService(typeof<AutoLoopManager>) :?> AutoLoopManager
    Assert.NotNull(man)
    let group = {
       Swap.Group.PairId = PairId(SupportedCryptoCode.BTC, SupportedCryptoCode.BTC)
       Swap.Group.Category = Swap.Category.Out
     }

    let chanId = ShortChannelId.FromUInt64(1UL)
    match! man.SetParameters(group, Parameters.Default(group.PairId)) with
    | Error e -> failwith e
    | Ok() ->
    let startParams = man.Parameters.[group]
    let startParams = {
      startParams with
        Rules = {
          startParams.Rules with
            ChannelRules =
              let newRule = { ThresholdRule.MinimumIncoming = 1s<percent>
                              MinimumOutGoing = 1s<percent> }
              startParams.Rules.ChannelRules
              |> Map.add(chanId) newRule
        }
    }
    let originalRule = {
      ThresholdRule.MinimumIncoming = 10s<percent>
      MinimumOutGoing = 10s<percent>
    }
    ()
  }

  static member RestrictionsValidationTestData: obj[] seq =
    seq {
      ("Ok", 1, 10, 1, 10000, None)
      ("Client invalid", 100, 1, 1, 10000, AutoLoopError.MinimumExceedsMaximumAmt |> Some)
      ("maximum exceeds server", 1, 2000, 1000, 1500, AutoLoopError.MaxExceedsServer(2000L |> Money.Satoshis, 1500L |> Money.Satoshis) |> Some)
      ("minimum less than server", 500, 1000, 1000, 1500, AutoLoopError.MinLessThenServer(500L |> Money.Satoshis, 1000L |> Money.Satoshis) |> Some)
    }
    |> Seq.map(fun (name, cMin, cMax, sMin, sMax, maybeExpectedErr) ->
      let cli = { Restrictions.Maximum = cMax |> int64 |> Money.Satoshis; Minimum = cMin |> int64 |> Money.Satoshis }
      let server = { Restrictions.Maximum = sMax |> int64 |> Money.Satoshis; Minimum = sMin |> int64 |> Money.Satoshis }
      [|
         name |> box
         cli |> box
         server |> box
         maybeExpectedErr |> box
      |]
    )

  [<Theory>]
  [<MemberData(nameof(AutoLoopTests.RestrictionsValidationTestData))>]
  member this.TestValidateRestrictions(name: string, client, server, maybeExpectedErr: AutoLoopError option) =
    match Restrictions.Validate(server, client), maybeExpectedErr with
    | Ok (), None -> ()
    | Ok (), Some e ->
      failwith $"{name}: expected error ({e}), but there was none."
    | Error e, None ->
      failwith $"{name}: expected Ok, but there was error {e}"
    | Error actualErr, Some expectedErr ->
      Assert.Equal(expectedErr, actualErr)

  member private this.TestSuggestSwaps() =
    ()


  [<Fact>]
  member this.RestrictedSuggestions() =
    ()

  [<Fact>]
  member this.TestSweepFeeLimit() =
    ()

  [<Fact>]
  member this.TestFeeLimits() =
    ()

  [<Fact>]
  member this.TestFeeBudget() =
    ()

  [<Fact>]
  member this.TestInFlightLimit() =
    ()

  [<Fact>]
  member this.TestSizeRestrictions() =
    ()
