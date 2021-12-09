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
open Microsoft.Extensions.Internal
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.DataEncoders
open NBitcoin.RPC
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Server.DTOs
open NLoop.Server.Projections
open NLoop.Server.RPCDTOs
open NLoop.Server.Services
open NLoop.Server.SwapServerClient
open NLoop.Server.SwapServerClient
open NLoop.Server.Tests.Extensions
open Org.BouncyCastle.Asn1.IsisMtt.X509
open Xunit


[<AutoOpen>]
module private Constants =
  let peer1 = PubKey("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619")
  let peer2 = PubKey("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c")

  let testTime = DateTimeOffset(2021, 12, 03, 23, 0, 0, TimeSpan.Zero)

  let chanId1 = ShortChannelId.FromUInt64(1UL)
  let chanId2 = ShortChannelId.FromUInt64(2UL)
  let chanId3 = ShortChannelId.FromUInt64(3UL)
  let channel1 = {
    LndClient.ListChannelResponse.Id = chanId1
    Cap = Money.Satoshis(10000L)
    LocalBalance = Money.Satoshis(10000L)
    NodeId = peer1
  }
  let pairId = PairId(SupportedCryptoCode.BTC, SupportedCryptoCode.BTC)
  let channel2 = {
    LndClient.ListChannelResponse.Id = chanId2
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
    OutgoingChannelIds = [| chanId1 |]
    Address = None
    PairId = pairId |> Some
    SwapTxConfRequirement = pairId.DefaultLoopOutParameters.SwapTxConfRequirement.Value |> int |> Some
    Label = None
    MaxSwapRoutingFee = routingFee |> ValueSome
    MaxPrepayRoutingFee = prepayFee |> ValueSome
    MaxSwapFee = testQuote.SwapFee |> ValueSome
    MaxPrepayAmount = testQuote.PrepayAmount |> ValueSome
    MaxMinerFee = testQuote.SweepMinerFee |> AutoLoopHelpers.scaleMinerFee |> ValueSome
    SweepConfTarget = pairId.DefaultLoopOutParameters.SweepConfTarget.Value |> int |> ValueSome
  }

  let chan2Rec = {
    chan1Rec
      with
      OutgoingChannelIds = [| chanId2 |]
  }

  let getDummyTestInvoice(network: Network) =
    assert(network <> null)
    let paymentPreimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
    let paymentHash = paymentPreimage.Hash
    let fields = { TaggedFields.Fields = [ PaymentHashTaggedField paymentHash; DescriptionTaggedField "test" ] }
    PaymentRequest.TryCreate(network, Some(LNMoney.Satoshis(100000L)), DateTimeOffset.UtcNow, fields, new Key())
    |> ResultUtils.Result.deref
    |> fun x -> x.ToString()

  let hex = HexEncoder()
  let loopOut1 =
    let claimKey =
      new Key(hex.DecodeData("0101010101010101010101010101010101010101010101010101010101010101"))
    let refundKey =
      new Key(hex.DecodeData("0202020202020202020202020202020202020202020202020202020202020202"))
    let paymentPreimage =
      hex.DecodeData("0909090909090909090909090909090909090909090909090909090909090909")
      |> PaymentPreimage.Create
    let paymentHash = paymentPreimage.Hash
    let timeoutBlockHeight = BlockHeight(32u)
    let chainName = Network.RegTest.ChainName
    let quoteN = pairId.Quote.ToNetworkSet().GetNetwork(chainName)
    let baseN = pairId.Base.ToNetworkSet().GetNetwork(chainName)
    {
      LoopOut.Id = Guid.NewGuid().ToString() |> SwapId
      OutgoingChanIds = [||]
      SwapTxConfRequirement = BlockHeightOffset32(3u)
      ClaimKey = claimKey
      Preimage = paymentPreimage
      RedeemScript =
        Scripts.reverseSwapScriptV1(paymentHash) claimKey.PubKey refundKey.PubKey timeoutBlockHeight
      Invoice =
        let fields = { TaggedFields.Fields = [ PaymentHashTaggedField paymentHash; DescriptionTaggedField "test" ] }
        PaymentRequest.TryCreate(quoteN, Some(LNMoney.Satoshis(100000L)), DateTimeOffset.UtcNow, fields, new Key())
        |> ResultUtils.Result.deref
        |> fun p -> p.ToString()
      ClaimAddress =
        claimKey.PubKey.WitHash.GetAddress(baseN).ToString()
      OnChainAmount = Money.Satoshis(10000L)
      TimeoutBlockHeight = timeoutBlockHeight
      LockupTransactionHex = None
      LockupTransactionHeight = None
      ClaimTransactionId = None
      IsClaimTxConfirmed = false
      IsOffchainOfferResolved = false
      PairId = pairId
      Label = String.Empty
      PrepayInvoice = getDummyTestInvoice(quoteN)
      SweepConfTarget = pairId.DefaultLoopOutParameters.SweepConfTarget
      MaxMinerFee = pairId.DefaultLoopOutParameters.MaxMinerFee
      ChainName = chainName.ToString()
      Cost = SwapCost.Zero
    }

  let chan1Out =
    { loopOut1 with OutgoingChanIds = [| chanId1 |] }

  let applyFeeCategoryQuote
    (minerFee: Money, prepayPPM: int64<ppm>, routingPPM: int64<ppm>, quote: SwapDTO.LoopOutQuote)
    (req: LoopOutRequest): LoopOutRequest =
    {
      req
        with
        MaxPrepayRoutingFee = ppmToSat(quote.PrepayAmount, prepayPPM) |> ValueSome
        MaxSwapRoutingFee = ppmToSat(req.Amount, routingPPM) |> ValueSome
        MaxSwapFee = quote.SwapFee |> ValueSome
        MaxPrepayAmount = quote.PrepayAmount |> ValueSome
        MaxMinerFee = minerFee |> ValueSome
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
    ServerRestrictions.Minimum = Money.Satoshis 1L
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
       Swap.Group.PairId = pairId
       Swap.Group.Category = Swap.Category.Out
     }

    match! man.SetParameters(group, Parameters.Default(group.PairId)) with
    | Error e -> failwith $"{e}"
    | Ok() ->
    let setChanRule chanId newRule (p: Parameters) =
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
    | Error e -> failwith $"{e}"
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
      Assert.Equal(AutoLoopError.InvalidParameters "Channel has 0 channel id", e)
  }

  static member RestrictionsValidationTestData: obj[] seq =
    seq {
      ("Ok", Some 1, Some 10, 1, 10000, None)
      ("Client invalid", Some 100, Some 1, 1, 10000, RestrictionError.MinimumExceedsMaximumAmt |> Some)
      ("maximum exceeds server", None, Some 2000, 1000, 1500, RestrictionError.MaxExceedsServer(2000L |> Money.Satoshis, 1500L |> Money.Satoshis) |> Some)
      ("minimum less than server", Some 500, None, 1000, 1500, RestrictionError.MinLessThenServer(500L |> Money.Satoshis, 1000L |> Money.Satoshis) |> Some)
    }
    |> Seq.map(fun (name, cMin, cMax, sMin, sMax, maybeExpectedErr) ->
      let cli = { ClientRestrictions.Maximum = cMax |> Option.map(int64 >> Money.Satoshis); Minimum = cMin |> Option.map(int64 >> Money.Satoshis)}
      let server = { ServerRestrictions.Maximum = sMax |> int64 |> Money.Satoshis; Minimum = sMin |> int64 |> Money.Satoshis }
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
    match ServerRestrictions.Validate(server, client), maybeExpectedErr with
    | Ok (), None -> ()
    | Ok (), Some e ->
      failwith $"{name}: expected error ({e}), but there was none."
    | Error e, None ->
      failwith $"{name}: expected Ok, but there was error {e}"
    | Error actualErr, Some expectedErr ->
      Assert.Equal(expectedErr, actualErr)

  member private this.TestSuggestSwapsCore(name: string, injection: IServiceCollection -> unit, group, parameters: Parameters, channels, expected: Result<SwapSuggestions, AutoLoopError>) = task {
    let i = fun (services: IServiceCollection) ->
      let dummySwapServerClient =
        TestHelpers.GetDummySwapServerClient
          {
            DummySwapServerClientParameters.Default
              with
              LoopOutQuote = fun _ -> testQuote
          }
      let dummyLnClientProvider =
        TestHelpers.GetDummyLightningClientProvider
          {
            DummyLnClientParameters.ListChannels = channels
          }
      let f = {
        new IFeeEstimator
          with
          member this.Estimate _target _cc =
            pairId.DefaultLoopOutParameters.SweepFeeRateLimit
            |> Task.FromResult
      }
      services
        .AddSingleton<IFeeEstimator>(f)
        .AddSingleton<ISwapServerClient>(dummySwapServerClient)
        .AddSingleton<ISystemClock>({ new ISystemClock with member this.UtcNow = testTime })
        .AddSingleton<ISwapActor>(mockSwapActor)
        .AddSingleton<ILightningClientProvider>(dummyLnClientProvider)
        |> ignore
      injection services
    use server = new TestServer(TestHelpers.GetTestHost(i))
    let man = server.Services.GetService(typeof<AutoLoopManager>) :?> AutoLoopManager
    Assert.NotNull(man)
    match! man.SetParameters(group, parameters) with
    | Error e ->
      match expected with
      | Error expectedErr ->
        Assert.Equal(expectedErr, e)
      | _  ->
        failwith $"{name}: Failed to set parameters {e}"
    | Ok() ->
      let! actual = man.SuggestSwaps(false, group)
      Assertion.isSame(expected, actual)
  }

  static member RestrictedSuggestionTestData =
    seq {
      let chanRules =
        [
          (chanId1, chanRule)
          (chanId2, chanRule)
        ]
        |> Map.ofSeq
      let rules = { Rules.Zero with ChannelRules = chanRules }

      let failureWithInTimeout chanId m =
        m |> Map.add chanId (testTime - defaultFailureBackoff + TimeSpan.FromSeconds(1.))
      let failureBeforeBackoff chanId m =
        m |> Map.add chanId (testTime - defaultFailureBackoff - TimeSpan.FromSeconds(1.))

      let expected = {
        SwapSuggestions.Zero
          with
            OutSwaps = [chan1Rec]
      }
      ("no existing swaps", seq [channel1], Seq.empty, rules, Map.empty, Map.empty, 2, expected)
      let swapState = seq [
        Swap.State.Out(BlockHeight.One, { loopOut1 with OutgoingChanIds = [||] })
      ]
      ("unrestricted loop out (should not affect the suggestion)", seq [channel1], swapState, rules, Map.empty, Map.empty, 2, expected)
      let expected = {
        SwapSuggestions.Zero
          with
          DisqualifiedChannels =
            [(chanId1, SwapDisqualifiedReason.InFlightLimitReached)] |> Map.ofSeq
      }
      ("Max auto inflight limit (should prevent swap)", seq [channel1], swapState, rules, Map.empty, Map.empty, 1, expected)
      let swapState = seq [
        Swap.State.Out(BlockHeight.One, chan1Out)
      ]
      let expected = {
        SwapSuggestions.Zero
          with
            OutSwaps = [ chan2Rec ]
            DisqualifiedChannels =
              [(chanId1, SwapDisqualifiedReason.LoopOutAlreadyInTheChannel)] |> Map.ofSeq
      }
      ("restricted loop out", seq [channel1; channel2], swapState, rules, Map.empty, Map.empty, 2, expected)
      let recentFailure =
        Map.empty |> failureWithInTimeout chanId1
      let expected = {
        SwapSuggestions.Zero
          with
            DisqualifiedChannels =
              [(chanId1, SwapDisqualifiedReason.FailureBackoff)] |> Map.ofSeq
      }
      ("Swap failed recently", seq[ channel1 ], Seq.empty, rules, recentFailure, Map.empty, 2,  expected)
      let notRecentFailure =
        Map.empty |> failureBeforeBackoff chanId1
      let expected = {
        SwapSuggestions.Zero
          with
            OutSwaps = [chan1Rec]
      }
      ("Swap failed before cutoff", seq [ channel1 ], Seq.empty, rules, notRecentFailure, Map.empty, 2, expected)

      // -- peer --

      let channelForThePeer = {
        ListChannelResponse.Id = chanId3
        Cap = Money.Satoshis(10000L)
        LocalBalance = Money.Satoshis(10000L)
        NodeId = peer1
      }
      let existingSwapState = seq [ Swap.State.Out(BlockHeight.One, chan1Out) ]
      let rules = {
        Rules.Zero
          with
          PeerRules =
            let rule = {
              MinimumIncoming = 0s<percent>
              MinimumOutGoing = 50s<percent>
            }
            [(peer1 |> NodeId, rule)] |> Map.ofSeq
      }
      let expected = {
        SwapSuggestions.Zero
          with
          DisqualifiedPeers = [(peer1 |> NodeId, SwapDisqualifiedReason.LoopOutAlreadyInTheChannel)] |> Map.ofSeq
      }
      ("existing on peer's channel", seq [ channel1; channelForThePeer ], existingSwapState, rules, Map.empty, Map.empty, 2, expected)
      // -- --
    }
    |> Seq.map(fun (name: string, channels: ListChannelResponse seq, onGoingSwaps: Swap.State seq, rules: Rules, recentFailureOut: Map<ShortChannelId, DateTimeOffset>, recentFailureIn: Map<NodeId, DateTimeOffset>, maxAutoInFlight, expected) ->
      [|
        name |> box
        channels |> box
        onGoingSwaps |> box
        rules |> box
        recentFailureOut |> box
        recentFailureIn |> box
        maxAutoInFlight |> box
        expected |> box
      |])

  [<Theory>]
  [<MemberData(nameof(AutoLoopTests.RestrictedSuggestionTestData))>]
  member this.RestrictedSuggestions(name: string,
                                    channels: ListChannelResponse seq,
                                    ongoingSwaps: Swap.State seq,
                                    rules: Rules,
                                    recentFailureOut: Map<ShortChannelId, DateTimeOffset>,
                                    recentFailureIn: Map<NodeId, DateTimeOffset>,
                                    maxAutoInFlight: int,
                                    expected: SwapSuggestions) =
    let group = {
      Swap.Group.PairId = pairId
      Swap.Group.Category = Swap.Category.Out
    }
    let parameters = {
      Parameters.Default(group.PairId)
        with
        Rules = rules
        MaxAutoInFlight = maxAutoInFlight
    }
    let setup = fun (services: IServiceCollection) ->
      let stateView = {
          new ISwapStateProjection with
            member this.State =
              ongoingSwaps
              |> Seq.fold(fun acc t -> acc |> Map.add (StreamId.Create "swap-" (Guid.NewGuid())) t) Map.empty
      }
      let failureView = {
        new IRecentSwapFailureProjection with
          member this.FailedLoopOuts = recentFailureOut
          member this.FailedLoopIns = recentFailureIn
      }
      services
        .AddSingleton<ISwapStateProjection>(stateView)
        .AddSingleton<IRecentSwapFailureProjection>(failureView)
        |> ignore
    this.TestSuggestSwapsCore(name, setup, group, parameters, channels |> Seq.toList, Ok expected)

  static member TestSweepFeeLimitTestData =
    let quote = {
      SwapDTO.LoopOutQuote.SwapFee = Money.Satoshis(1L)
      SwapDTO.SweepMinerFee = Money.Satoshis(50L)
      SwapDTO.SwapPaymentDest = peer1
      SwapDTO.CltvDelta = BlockHeightOffset32(5u)
      SwapDTO.PrepayAmount = Money.Satoshis(500L)
    }
    seq [
      let expected = {
        SwapSuggestions.Zero
          with
          OutSwaps = [
            applyFeeCategoryQuote
              (pairId.DefaultLoopOutParameters.MaxMinerFee, defaultMaxPrepayRoutingFeePPM, defaultMaxRoutingFeePPM, quote)
              chan1Rec
          ]
      }
      ("fee estimate ok", pairId.DefaultLoopOutParameters.SweepFeeRateLimit, expected)
      let ourLimit = pairId.DefaultLoopOutParameters.SweepFeeRateLimit
      let actualFee = FeeRate(ourLimit.FeePerK + Money.Satoshis(1L))
      let expected = {
        SwapSuggestions.Zero
          with
          DisqualifiedChannels =
            Map.ofSeq [(chanId1, SwapDisqualifiedReason.SweepFeesTooHigh({| Estimation = actualFee; OurLimit = ourLimit |}))]
      }
      ("fee estimate above limit", actualFee, expected)
    ]
    |> Seq.map(fun (name, feerate, expected) -> [|
      name |> box
      feerate |> box
      quote |> box
      expected |> box
    |])

  [<Theory>]
  [<MemberData(nameof(AutoLoopTests.TestSweepFeeLimitTestData))>]
  member this.TestSweepFeeLimit(name: string, feeRate: FeeRate, quote, expected) =
    let setup (services: IServiceCollection) =
      let f = {
        new IFeeEstimator
          with
          member this.Estimate _target _cc =
            feeRate |> Task.FromResult
      }
      let dummyLoopServerClient =
        TestHelpers.GetDummySwapServerClient
          {
            DummySwapServerClientParameters.Default
              with
              LoopOutQuote = fun _ -> quote
          }
      services
        .AddSingleton<IFeeEstimator>(f)
        .AddSingleton<ISwapServerClient>(dummyLoopServerClient)
        |> ignore
    let group = {
      Swap.Group.Category = Swap.Category.Out
      Swap.Group.PairId = pairId
    }
    let parameters = {
      Parameters.Default(pairId)
        with
        FeeLimit = FeeCategoryLimit.Default pairId
        Rules = { Rules.Zero with ChannelRules = Map.ofSeq [(chanId1, chanRule)] }
    }
    this.TestSuggestSwapsCore(name, setup, group, parameters, [channel1], Ok expected)

  static member TestSuggestSwapsTestData =
    seq[
      ("no rules", [channel1], Rules.Zero, Error(AutoLoopError.NoRules))
      let r = {
        Rules.Zero with
          ChannelRules = Map.ofSeq[(chanId1, chanRule)]
      }
      let expected = {
        SwapSuggestions.Zero
          with
          OutSwaps = [ chan1Rec ]
      }
      ("loop out", [channel1], r, Ok expected)
      let r = { Rules.Zero with ChannelRules = Map.ofSeq[(chanId2, { ThresholdRule.MinimumIncoming = 10s<percent>; MinimumOutGoing = 10s<percent> })] }
      ("no rule for the channel", [channel1], r, Ok (SwapSuggestions.Zero))
      let channels = [
        { ListChannelResponse.NodeId = peer1
          Cap = 20000L |> Money.Satoshis
          LocalBalance = 8000L |> Money.Satoshis
          Id = chanId1 }
        { ListChannelResponse.NodeId = peer1
          Cap = 10000L |> Money.Satoshis
          LocalBalance = 9000L |> Money.Satoshis
          Id = chanId2 }
        { ListChannelResponse.NodeId = peer2
          Cap = 5000L |> Money.Satoshis
          LocalBalance = 2000L |> Money.Satoshis
          Id = chanId3 }
      ]
      let r = {
        Rules.Zero
          with
          PeerRules = Map.ofSeq[
            (peer1 |> NodeId, { MinimumIncoming = 80s<percent>; MinimumOutGoing = 0s<percent> })
            (peer2 |> NodeId, { MinimumIncoming = 40s<percent>; MinimumOutGoing = 50s<percent> })
          ]
      }
      let expected = {
        SwapSuggestions.Zero
          with
          OutSwaps = [
            let expectedAmount = Money.Satoshis(10000L)
            let prepay, routing = testPPMFees(defaultFeePPM, testQuote, expectedAmount)
            { LoopOutRequest.Amount = expectedAmount
              OutgoingChannelIds = [|chanId1; chanId2|]
              Address = None
              PairId = pairId |> Some
              SwapTxConfRequirement =
                pairId.DefaultLoopOutParameters.SwapTxConfRequirement.Value |> int |> Some
              Label = None
              MaxSwapRoutingFee = routing |> ValueSome
              MaxPrepayRoutingFee = prepay |> ValueSome
              MaxSwapFee = testQuote.SwapFee |> ValueSome
              MaxPrepayAmount = testQuote.PrepayAmount |> ValueSome
              MaxMinerFee = scaleMinerFee(testQuote.SweepMinerFee) |> ValueSome
              SweepConfTarget =
                pairId.DefaultLoopOutParameters.SweepConfTarget.Value |> int |> ValueSome }
          ]
          DisqualifiedPeers = Map.ofSeq[(peer2 |> NodeId, SwapDisqualifiedReason.LiquidityOk)]
      }
      ("multiple peer rules", channels, r, Ok expected)
    ]
    |> Seq.map(fun (name: string,
                    channels: ListChannelResponse list,
                    rules: Rules,
                    r: Result<SwapSuggestions, AutoLoopError>) -> [|
      name |> box
      channels |> box
      rules |> box
      r |> box
    |])

  [<Theory>]
  [<MemberData(nameof(AutoLoopTests.TestSuggestSwapsTestData))>]
  member this.TestSuggestSwaps(name: string,
                               channels: ListChannelResponse list,
                               rules: Rules,
                               expected: Result<SwapSuggestions, AutoLoopError>) =
    let setup (services: IServiceCollection) =
      ()

    let group = {
       Swap.Group.PairId = pairId
       Swap.Group.Category = Swap.Category.Out
     }

    let parameters = {
      Parameters.Default(pairId)
        with
        Rules = rules
    }
    this.TestSuggestSwapsCore(name, setup, group, parameters, channels, expected)

  static member TestFeeLimitsTestData =
    seq [
      let quoteBase = {
        SwapDTO.LoopOutQuote.SwapFee = Money.Satoshis(1L)
        SwapDTO.SweepMinerFee = Money.Satoshis(50L)
        SwapDTO.SwapPaymentDest = peer1
        SwapDTO.CltvDelta = BlockHeightOffset32(5u)
        SwapDTO.PrepayAmount = Money.Satoshis(500L)
      }
      let expected = {
        SwapSuggestions.Zero
          with
          OutSwaps = [
            applyFeeCategoryQuote
              (pairId.DefaultLoopOutParameters.MaxMinerFee, defaultMaxPrepayRoutingFeePPM, defaultMaxRoutingFeePPM, quoteBase)
              chan1Rec
          ]
      }
      ("fees ok", quoteBase, expected)
      let ourMaxPrepay = pairId.DefaultLoopOutParameters.MaxPrepay
      let serverRequirement = ourMaxPrepay + Money.Satoshis(1L)
      let quote = {
        quoteBase
          with
          SwapDTO.PrepayAmount = serverRequirement
      }
      let expected = {
        SwapSuggestions.Zero
          with
          DisqualifiedChannels =
            Map.ofSeq[(chanId1, SwapDisqualifiedReason.PrepayTooHigh({| ServerRequirement = serverRequirement; OurLimit = ourMaxPrepay |}))]
      }
      ("insufficient prepay", quote, expected)
      let ourLimit = pairId.DefaultLoopOutParameters.MaxMinerFee
      let serverRequirement = ourLimit + Money.Satoshis(1L)
      let quote = {
        quoteBase
          with
          SwapDTO.SweepMinerFee = serverRequirement
      }
      let expected = {
        SwapSuggestions.Zero
          with
          DisqualifiedChannels = Map.ofSeq [(chanId1, SwapDisqualifiedReason.MinerFeeTooHigh({| ServerRequirement = serverRequirement; OurLimit = ourLimit |}))]
      }
      ("insufficient miner fee", quote, expected)
      let ourLimit = ppmToSat(swapAmount, pairId.DefaultLoopOutParameters.MaxSwapFeePPM)
      let serverRequirement = ourLimit + Money.Satoshis(1L)
      let quote = {
        quoteBase
          with
          SwapDTO.SwapFee = serverRequirement
      }
      let expected = {
        SwapSuggestions.Zero
          with
          DisqualifiedChannels =
            Map.ofSeq[(chanId1, SwapDisqualifiedReason.SwapFeeTooHigh({| ServerRequirement = serverRequirement; OurLimit = ourLimit |}))]
      }
      ("insufficient swap fee", quote, expected)
    ]
    |> Seq.map(fun (name, quote, expected) -> [|
      name |> box
      quote |> box
      expected |> box
    |])

  [<Theory>]
  [<MemberData(nameof(AutoLoopTests.TestFeeLimitsTestData))>]
  member this.TestFeeLimits(name: string, quote, expected: SwapSuggestions) =
    let setup (services: IServiceCollection) =
      let swapServerClient =
        TestHelpers.GetDummySwapServerClient
          {
            DummySwapServerClientParameters.Default
              with
              LoopOutQuote = fun _ -> quote
          }
      services
        .AddSingleton<ISwapServerClient>(swapServerClient)
        |> ignore
    let group = {
       Swap.Group.PairId = pairId
       Swap.Group.Category = Swap.Category.Out
     }

    let parameters = {
      Parameters.Default(pairId)
        with
        FeeLimit = FeeCategoryLimit.Default pairId
        Rules = { Rules.Zero with ChannelRules = Map.ofSeq [(chanId1, chanRule)] }
    }
    this.TestSuggestSwapsCore(name, setup, group, parameters, [channel1], Ok expected)

  static member TestInFlightLimitTestData =
    seq [
      let expected = {
        SwapSuggestions.Zero
          with
          OutSwaps = [chan1Rec; chan2Rec]
      }
      ("none in flight, extra space", 3, Seq.empty, Rules.Zero, expected)
      ("none in flight, exact match", 2, Seq.empty, Rules.Zero, expected)
      let o = {
        loopOut1
          with
          Label = Labels.autoLoopLabel(Swap.Category.Out)
      }
      let existing = seq {
        Swap.State.Out(BlockHeight.One, o)
      }
      let expected = {
        SwapSuggestions.Zero
          with
          DisqualifiedChannels =
            Map.ofSeq [
              (chanId1, SwapDisqualifiedReason.InFlightLimitReached)
              (chanId2, SwapDisqualifiedReason.InFlightLimitReached)
            ]
      }
      ("max in flight", 1, existing, Rules.Zero, expected)
      let existing = seq {
        Swap.State.Out(BlockHeight.One, o)
        Swap.State.Out(BlockHeight.One, o)
      }
      ("max swaps exceeded", 1, existing, Rules.Zero, expected)
      let existing = seq {
        Swap.State.Out(BlockHeight.One, o)
      }
      let rules = {
        Rules.Zero
          with
          // create two peer-level rules, both in need of a swap,
          // but peer 1 needs a larger swap so will be prioritized.
          PeerRules = Map.ofSeq[
            (peer1 |> NodeId, { ThresholdRule.MinimumIncoming = 50s<percent>; MinimumOutGoing = 0s<percent> })
            (peer2 |> NodeId, { ThresholdRule.MinimumIncoming = 40s<percent>; MinimumOutGoing = 0s<percent> })
          ]
      }
      let expected = {
        SwapSuggestions.Zero
          with
          OutSwaps = [chan1Rec]
          DisqualifiedPeers = Map.ofSeq[
            (peer2 |> NodeId, SwapDisqualifiedReason.InFlightLimitReached)
          ]
      }
      ("peer rules max swaps exceeded", 2, existing, rules, expected)
    ]
    |> Seq.map(fun (name, maxInFlight, existingSwaps: Swap.State seq, rules, expected) -> [|
      name |> box
      maxInFlight |> box
      existingSwaps |> box
      rules |> box
      expected |> box
    |])

  [<Theory>]
  [<MemberData(nameof(AutoLoopTests.TestInFlightLimitTestData))>]
  member this.TestInFlightLimit(name, maxInFlight, existingSwaps, rules: Rules, expected) =
    let setup (services: IServiceCollection) =
      let swapState = {
        new ISwapStateProjection with
          member this.State =
            existingSwaps
            |> Seq.fold(fun acc t -> acc |> Map.add (StreamId.Create "swap-" (Guid.NewGuid())) t) Map.empty
      }
      services
        .AddSingleton<ISwapStateProjection>(swapState)
        |> ignore
    let group = {
       Swap.Group.PairId = pairId
       Swap.Group.Category = Swap.Category.Out
     }
    let parameters = {
      Parameters.Default(pairId)
        with
        MaxAutoInFlight = maxInFlight
        Rules =
          if rules = Rules.Zero then
            {
              rules
                with
                ChannelRules = Map.ofSeq[(chanId1, chanRule); (chanId2, chanRule)]
            }
          else
            rules
    }
    this.TestSuggestSwapsCore(name, setup, group, parameters, [channel1; channel2], Ok expected)

  static member TestSizeRestrictionsTestData =
    seq [
      let serverTerms = {
        SwapDTO.OutTermsResponse.MinSwapAmount = 6000L |> Money.Satoshis
        SwapDTO.MaxSwapAmount = 10000L |> Money.Satoshis
      }
      let expected = {
        SwapSuggestions.Zero
          with
          OutSwaps = [chan1Rec]
      }
      ("minimum more than server, swap happens", Some 7000L, None, [|serverTerms; serverTerms|], Ok expected)
      let expected = {
        SwapSuggestions.Zero
          with
          DisqualifiedChannels = Map.ofSeq [(chanId1, SwapDisqualifiedReason.LiquidityOk)]
      }
      ("minimum more than server, no swap", Some(8000L), None, [|serverTerms; serverTerms|], Ok expected)

      let clientMax = 7000L
      let prepay, routing = testPPMFees(pairId.DefaultLoopOutParameters.MaxSwapFeePPM, testQuote, 7000L |> Money.Satoshis)
      let outSwap = {
        chan1Rec
          with
          Amount = Money.Satoshis clientMax
          MaxPrepayRoutingFee = prepay |> ValueSome
          MaxSwapRoutingFee = routing |> ValueSome
      }
      let expected = {
        SwapSuggestions.Zero
          with
          OutSwaps = [outSwap]
      }
      ("maximum less than server, swap happens", None, Some clientMax, [|serverTerms; serverTerms|], Ok expected)
      let serverMax = 6000L
      let clientMax = 9000L
      let serverT = {
        SwapDTO.OutTermsResponse.MinSwapAmount = 5000L |> Money.Satoshis
        SwapDTO.MaxSwapAmount = serverMax |> Money.Satoshis
      }
      let e =
        AutoLoopError.InvalidParameters
          "maximum swap amount (9000 sats) is more than the server maximum (6000 sats)"
      ("client params stale over time", Some(6500L), Some(clientMax), [| serverT |], Error e)

    ]
    |> Seq.map(fun (name: string, clientMin: int64 option, clientMax: int64 option, serverR: SwapDTO.OutTermsResponse[], expected: Result<_, AutoLoopError>) -> [|
      name |> box
      { ClientRestrictions.Minimum = clientMin |> Option.map Money.Satoshis;
        Maximum = clientMax |> Option.map Money.Satoshis } |> box
      serverR |> box
      expected |> box
    |])

  [<Theory>]
  [<MemberData(nameof(AutoLoopTests.TestSizeRestrictionsTestData))>]
  member this.TestSizeRestrictions(name: string,
                                   clientR: ClientRestrictions,
                                   outTerms: SwapDTO.OutTermsResponse[],
                                   expected: Result<_, _>) =
    let mutable callCount = 0
    let setup (services: IServiceCollection) =
      let swapClient =
        TestHelpers.GetDummySwapServerClient
          {
            DummySwapServerClientParameters.Default
              with
                LoopOutQuote = fun _ -> testQuote
                LoopOutTerms = fun _ ->
                  let r = outTerms.[callCount]
                  callCount <- callCount + 1
                  r
          }
      services
        .AddSingleton<ISwapServerClient>(swapClient)
        |> ignore
    let group = {
       Swap.Group.PairId = pairId
       Swap.Group.Category = Swap.Category.Out
     }
    let parameters = {
      Parameters.Default(pairId)
        with
        ClientRestrictions = clientR
        Rules = { Rules.Zero with ChannelRules = Map.ofSeq[(chanId1, chanRule)] }
    }
    task {
      do! this.TestSuggestSwapsCore(name, setup, group, parameters, [channel1], expected)
      Assert.Equal(outTerms.Length, callCount)
    }

  static member TestFeePercentageTestData =
    seq [
      let okPPM = 30000L<ppm>
      let okQuote = {
        SwapDTO.LoopOutQuote.SwapFee = Money.Satoshis(15L)
        SwapDTO.SweepMinerFee = Money.Satoshis(1L)
        SwapDTO.SwapPaymentDest = peer1
        SwapDTO.CltvDelta = BlockHeightOffset32(5u)
        SwapDTO.PrepayAmount = Money.Satoshis(30L)
      }
      let maxPrepayRoutingFee, maxSwapRoutingFee = testPPMFees(okPPM, okQuote, 7500L |> Money.Satoshis)
      let req = {
        LoopOutRequest.Amount = 7500L |> Money.Satoshis
        OutgoingChannelIds = [|chanId1|]
        Address = None
        PairId = pairId |> Some
        SwapTxConfRequirement =
          pairId.DefaultLoopOutParameters.SwapTxConfRequirement.Value |> int |> Some
        Label = None
        MaxSwapRoutingFee = maxSwapRoutingFee |> ValueSome
        MaxPrepayRoutingFee = maxPrepayRoutingFee |> ValueSome
        MaxSwapFee = okQuote.SwapFee |> ValueSome
        MaxPrepayAmount = okQuote.PrepayAmount |> ValueSome
        MaxMinerFee = okQuote.SweepMinerFee |> AutoLoopHelpers.scaleMinerFee |> ValueSome
        SweepConfTarget = pairId.DefaultLoopOutParameters.SweepConfTarget.Value |> int |> ValueSome
      }

      ("fees ok", okPPM, okQuote, { SwapSuggestions.Zero with OutSwaps = [req] })
      let quote = {
        okQuote
          with
          SwapDTO.LoopOutQuote.SwapFee = Money.Satoshis(300L)
          SwapDTO.PrepayAmount = Money.Satoshis(30L)
          SwapDTO.SweepMinerFee = Money.Satoshis(1L)
      }
      let feePPM = 20000L<ppm>
      let expected = {
        SwapSuggestions.Zero
          with
          DisqualifiedChannels =
            Map.ofSeq[
              (chanId1, SwapDisqualifiedReason.SwapFeeTooHigh({|ServerRequirement = quote.SwapFee
                                                                OurLimit = ppmToSat(swapAmount, feePPM) |}))
            ]
      }
      ("swap fee too high", feePPM, quote, expected)
      let quote = {
        okQuote
          with
          SwapDTO.LoopOutQuote.SwapFee = Money.Satoshis(80L)
          SwapDTO.PrepayAmount = Money.Satoshis(30L)
          SwapDTO.SweepMinerFee = Money.Satoshis(300L)
      }
      let expected = {
        SwapSuggestions.Zero
          with
          DisqualifiedChannels =
            Map.ofSeq[
              (chanId1, SwapDisqualifiedReason.MinerFeeTooHigh({|ServerRequirement = scaleMinerFee quote.SweepMinerFee
                                                                 OurLimit = ppmToSat(swapAmount, feePPM) |}))
            ]
      }
      ("miner fee too high", feePPM, quote, expected)
      let quote = {
        okQuote
          with
          SwapDTO.LoopOutQuote.SwapFee = Money.Satoshis(75L)
          SwapDTO.PrepayAmount = Money.Satoshis(300L)
          SwapDTO.SweepMinerFee = Money.Satoshis(1L)
      }
      let feePPM = 30000L<ppm>
      let expected = {
        SwapSuggestions.Zero
          with
          DisqualifiedChannels =
            Map.ofSeq[
              (chanId1, SwapDisqualifiedReason.PrepayTooHigh({|ServerRequirement = quote.PrepayAmount
                                                               OurLimit = ppmToSat(swapAmount, feePPM) |}))
            ]
      }
      ("prepay too high", feePPM, quote, expected)
    ]
    |> Seq.map(fun (name: string, feePPM: int64<ppm>, quote: SwapDTO.LoopOutQuote, expected) -> [|
      name |> box
      feePPM |> box
      quote |> box
      expected |> box
    |])

  /// Tests use of a flat fee percentage to limit the fees we pay for swaps.
  /// Our test is setup to require a 7500 sat swap, and we test this amount against
  /// various fee percentages and server quotes.
  [<Theory>]
  [<MemberData(nameof(AutoLoopTests.TestFeePercentageTestData))>]
  member this.TestFeePercentage(name: string, feePPM: int64<ppm>, quote: SwapDTO.LoopOutQuote, expected) =
    let setup (services: IServiceCollection) =
      let swapClient =
        TestHelpers.GetDummySwapServerClient
          {
            DummySwapServerClientParameters.Default
              with
                LoopOutQuote = fun _ ->
                  quote
          }
      services
        .AddSingleton<ISwapServerClient>(swapClient)
        |> ignore
    let group = {
       Swap.Group.PairId = pairId
       Swap.Group.Category = Swap.Category.Out
     }
    let parameters = {
      Parameters.Default(pairId)
        with
        Rules = { Rules.Zero with ChannelRules = Map.ofSeq[(chanId1, chanRule)] }
        FeeLimit = { FeePortion.PartsPerMillion = feePPM }
    }
    this.TestSuggestSwapsCore(name, setup, group, parameters, [channel1], Ok expected)
