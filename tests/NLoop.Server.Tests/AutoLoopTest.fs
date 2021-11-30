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
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Server.DTOs
open NLoop.Server.Projections
open NLoop.Server.Services
open NLoop.Server.SwapServerClient
open NLoop.Server.Tests.Extensions
open NLoopClient
open Xunit


[<AutoOpen>]
module private Constants =
  let peer1 = PubKey("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619")
  let peer2 = PubKey("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c")
  let chanId1 = ShortChannelId.FromUInt64(1UL)
  let chanId2 = ShortChannelId.FromUInt64(2UL)
  let chanId3 = ShortChannelId.FromUInt64(3UL)
  let channel1 = {
    LndClient.ListChannelResponse.Id = chanId1
    Cap = Money.Satoshis(10000L)
    LocalBalance = Money.Satoshis(10000L)
    NodeId = peer1
  }
  let channel2 = {
    LndClient.ListChannelResponse.Id = chanId1
    Cap = Money.Satoshis(10000L)
    LocalBalance = Money.Satoshis(10000L)
    NodeId = peer2
  }
  let chanRule = {
    ThresholdRule.MinimumIncoming = 50s<percent>
    MinimumOutGoing = 0s<percent>
  }

  let testQuote = {
    SwapDTO.LoopOutQuote.SwapFee = Money.Satoshis(5L)
    SwapDTO.LoopOutQuote.SweepMinerFee = Money.Satoshis(1L)
    SwapDTO.LoopOutQuote.SwapPaymentDest = peer1
    SwapDTO.LoopOutQuote.CltvDelta = BlockHeightOffset32(20u)
    SwapDTO.LoopOutQuote.PrepayAmount = Money.Satoshis(50L) }

  let dummyAddr = BitcoinAddress.Create("bcrt1qjwfqxekdas249pr9fgcpxzuhmndv6dqlulh44m", Network.RegTest)

  let testPPMFees(ppm: int64<ppm>, quote: SwapDTO.LoopOutQuote, swapAmount: Money): Money * Money =
    let feeTotal = ppmToSat(swapAmount, ppm)
    let feeAvailable = feeTotal - scaleMinerFee(quote.SweepMinerFee) - quote.SwapFee
    AutoLoopHelpers.splitOffChain(feeAvailable, quote.PrepayAmount, swapAmount)

  let swapAmount = Money.Satoshis(7500L)
  let prepayFee, routingFee = testPPMFees(defaultFeePPM, testQuote, swapAmount)

  // this is the suggested swap for channel 1 when se use chanRule.
  let chan1Rec = {
    LoopOutRequest.Amount = swapAmount
    LoopOutRequest.OutgoingChannelIds = [| chanId1 |]
    LoopOutRequest.Address = None
    PairId = None
    SwapTxConfRequirement = None
    Label = None
    MaxSwapRoutingFee = routingFee |> ValueSome
    MaxPrepayRoutingFee = prepayFee |> ValueSome
    MaxSwapFee = testQuote.SwapFee |> ValueSome
    MaxPrepayAmount = testQuote.PrepayAmount |> ValueSome
    MaxMinerFee = testQuote.SweepMinerFee |> AutoLoopHelpers.scaleMinerFee |> ValueSome
    SweepConfTarget = ValueNone
  }

  let chan2Rec = {
    chan1Rec
      with
      OutgoingChannelIds = [| chanId2 |]
  }

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

  [<Fact>]
  member this.TestParameters() = task {
    use server = new TestServer(TestHelpers.GetTestHost(fun services ->
      services
        .AddSingleton<ISwapActor>(mockSwapActor)
        .AddSingleton<ISwapServerClient>(TestHelpers.GetDummySwapServerClient())
        |> ignore
    ))
    let man = server.Services.GetService(typeof<AutoLoopManager>) :?> AutoLoopManager
    Assert.NotNull(man)
    let group = {
       Swap.Group.PairId = PairId(SupportedCryptoCode.BTC, SupportedCryptoCode.BTC)
       Swap.Group.Category = Swap.Category.Out
     }

    match! man.SetParameters(group, Parameters.Default(group.PairId)) with
    | Error e -> failwith e
    | Ok() ->
    let setChanRule chanId newRule p =
      {
        p with
          Rules = {
            p.Rules with
              ChannelRules =
                p.Rules.ChannelRules
                |> Map.add(chanId) newRule
          }
      }
    let startParams = man.Parameters.[group]
    let newParams =
      let chanId = ShortChannelId.FromUInt64(1UL)
      let newRule = { ThresholdRule.MinimumIncoming = 1s<percent>
                      MinimumOutGoing = 1s<percent> }
      setChanRule chanId newRule startParams

    match! man.SetParameters(group, newParams) with
    | Error e -> failwith e
    | Ok () ->
    let p = man.Parameters.[group]
    Assert.NotEqual(startParams, p)
    Assert.Equal(newParams, p)


    let invalidParams =
      let invalidChanId = ShortChannelId.FromUInt64(0UL)
      let rule = {
        ThresholdRule.MinimumIncoming = 1s<percent>
        ThresholdRule.MinimumOutGoing = 1s<percent>
      }
      setChanRule invalidChanId rule p
    match! man.SetParameters(group, invalidParams) with
    | Ok () -> ()
    | Error e ->
      Assert.Equal("Channel has 0 channel id", e)
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

  static member RestrictedSuggestionTestData =
    seq {
      let chanRules =
        Map.empty
        |> Map.add chanId1 chanRule
        |> Map.add chanId2 chanRule

      let e = {
        SwapSuggestions.Zero
          with
            OutSwaps = [chan1Rec]
      }
      ("no existing swaps", channel1, Seq.empty, { Rules.Zero with ChannelRules = chanRules }, Map.empty, Map.empty, e)
      let s = // seq {
        // Swap.State.Out({ LoopOut })
      //}
        Seq.empty
      let e = {
        SwapSuggestions.Zero
          with
            OutSwaps = [chan1Rec]
      }
      ("unrestricted loop out", channel1, s, { Rules.Zero with ChannelRules = chanRules }, Map.empty, Map.empty, e)
    }
    |> Seq.map(fun (name: string, channels: ListChannelResponse, state: Swap.State seq, rules: Rules, recentFailureOut: Map<ShortChannelId, DateTime>, recentFailureIn: Map<NodeId, DateTime>, expected) ->
      [|
        name |> box
        channels |> box
        state |> box
        rules |> box
        recentFailureOut |> box
        recentFailureIn |> box
        expected |> box
      |])

  [<Theory>]
  [<MemberData(nameof(AutoLoopTests.RestrictedSuggestionTestData))>]
  member this.RestrictedSuggestions(name: string,
                                    channels: ListChannelResponse,
                                    swapStates: Swap.State seq,
                                    rules: Rules,
                                    recentFailureOut: Map<ShortChannelId, DateTime>,
                                    recentFailureIn: Map<NodeId, DateTime>,
                                    expected: SwapSuggestions) = unitTask {
    use server = new TestServer(TestHelpers.GetTestHost(fun services ->
      let stateView = {
          new ISwapStateProjection with
            member this.State =
              swapStates
              |> Seq.fold(fun acc t -> acc |> Map.add (StreamId.Create "swap-" (Guid.NewGuid())) t) Map.empty
      }
      let failureView = {
        new IRecentSwapFailureProjection with
          member this.FailedLoopOuts = recentFailureOut
          member this.FailedLoopIns = recentFailureIn
      }
      let dummyLightningClientProvider =
        TestHelpers.GetDummyLightningClientProvider
          {
            DummyLnClientParameters.Default
              with
              ListChannels = [channels]
          }
      services
        .AddSingleton<ISwapActor>(mockSwapActor)
        .AddSingleton<ISwapServerClient>(TestHelpers.GetDummySwapServerClient())
        .AddSingleton<ISwapStateProjection>(stateView)
        .AddSingleton<IRecentSwapFailureProjection>(failureView)
        .AddSingleton<ILightningClientProvider>(dummyLightningClientProvider)
        |> ignore
    ))
    let man = server.Services.GetService(typeof<AutoLoopManager>) :?> AutoLoopManager
    Assert.NotNull(man)
    let group = {
       Swap.Group.PairId = PairId(SupportedCryptoCode.BTC, SupportedCryptoCode.BTC)
       Swap.Group.Category = Swap.Category.Out
     }

    let p = {
      Parameters.Default(group.PairId)
        with
        Rules = rules
    }
    match! man.SetParameters(group, p) with
    | Error e -> failwith $"{name}: Failed to set parameters {e}"
    | Ok() ->
      match! man.SuggestSwaps(false, group) with
      | Ok r ->
        Assert.Equal(expected, r)
      | Error e -> failwith $"{name}: SuggestSwaps error {e}"
  }

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
