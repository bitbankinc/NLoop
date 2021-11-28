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
open NLoop.Server.SwapServerClient
open NLoop.Server.Tests.Extensions
open Xunit


type AutoLoopTests() =
  let mockSwapActor = {
    new ISwapActor with
      member this.ExecNewLoopOut(req, currentHeight) =
        failwith "todo"
      member this.ExecNewLoopIn(req, currentHeight) =
        failwith "todo"
      member this.Handler =
        failwith "todo"
      member this.Aggregate =
        failwith "todo"
      member this.Execute(swapId, msg, source) =
        failwith "todo"
  }

  let defaultTestRestrictions = {
    Restrictions.Minimum = Money.Satoshis 1L
    Maximum = Money.Satoshis 10000L
  }

  let getMockSwapServerClient
    (loopOutTermResponse)
    = {
    new ISwapServerClient with
      member this.LoopOut(request: SwapDTO.LoopOutRequest, ?ct: CancellationToken): Task<SwapDTO.LoopOutResponse> =
        failwith "todo"
      member this.LoopIn(request: SwapDTO.LoopInRequest, ?ct: CancellationToken): Task<SwapDTO.LoopInResponse> =
        failwith "todo"
      member this.GetNodes(?ct: CancellationToken): Task<SwapDTO.GetNodesResponse> =
        failwith "todo"

      member this.GetLoopOutQuote(request: SwapDTO.LoopOutQuoteRequest, ?ct: CancellationToken): Task<SwapDTO.LoopOutQuote> =
        {
          SwapDTO.LoopOutQuote.CltvDelta = ""
          SwapDTO.LoopOutQuote.SwapFee = failwith "todo"
          SwapDTO.LoopOutQuote.SweepMinerFee = failwith "todo"
          SwapDTO.LoopOutQuote.SwapPaymentDest = failwith "todo"
          SwapDTO.LoopOutQuote.PrepayAmount = failwith "todo"
        }
        |> Task.FromResult

      member this.GetLoopInQuote(request: SwapDTO.LoopInQuoteRequest, ?ct: CancellationToken): Task<SwapDTO.LoopInQuote> =
        failwith "todo"

      member this.GetLoopOutTerms(pairId: PairId, ?ct : CancellationToken): Task<SwapDTO.OutTermsResponse> =
        loopOutTermResponse
        |> Task.FromResult
      member this.GetLoopInTerms(pairId: PairId, ?ct : CancellationToken): Task<SwapDTO.InTermsResponse> =
        failwith "todo"
      member this.CheckConnection(?ct: CancellationToken): Task =
        failwith "todo"

      member this.ListenToSwapTx(swapId: SwapId, ?ct: CancellationToken): Task<Transaction> =
        failwith "todo"
  }

  [<Fact>]
  member this.TestParameters() = task {
    use server = new TestServer(TestHelpers.GetTestHost(fun services ->
      let loopOutTerms = {
        SwapDTO.OutTermsResponse.MaxSwapAmount = failwith "todo"
        SwapDTO.OutTermsResponse.MinSwapAmount = failwith "todo"
      }
      services
        .AddSingleton<ISwapActor>(mockSwapActor)
        .AddSingleton<ISwapServerClient>(getMockSwapServerClient loopOutTerms)
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
      ("Client invalid", 100, 1, 1, 10000, RestrictionError.MinimumExceedsMaximumAmt |> Some)
      ("maximum exceeds server", 1, 2000, 1000, 1500, RestrictionError.MaxExceedsServer(2000L |> Money.Satoshis, 1500L |> Money.Satoshis) |> Some)
      ("minimum less than server", 500, 1000, 1000, 1500, RestrictionError.MinLessThenServer(500L |> Money.Satoshis, 1000L |> Money.Satoshis) |> Some)
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
  member this.TestValidateRestrictions(name: string, client, server, maybeExpectedErr: RestrictionError option) =
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
