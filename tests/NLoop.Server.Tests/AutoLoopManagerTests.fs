namespace NLoop.Server.Tests

open System
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open LndClient
open FSharp.Control.Tasks
open Microsoft.AspNetCore.Authentication
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.DTOs
open NLoop.Server.Options
open NLoop.Server.Projections
open NLoop.Server.Services
open NLoop.Server.SwapServerClient
open Xunit

type private QuoteOutRequestResponse = (SwapDTO.LoopOutQuoteRequest * SwapDTO.LoopOutQuote) list
type private QuoteInRequestResponse = (SwapDTO.LoopInQuoteRequest * SwapDTO.LoopInQuote) list
type private LoopOutRequestResponse = (LoopOutRequest * LoopOutResponse) list
type private LoopInRequestResponse = (LoopInRequest * LoopInResponse) list
type private AutoLoopStep = {
  MinAmount: Money
  MaxAmount: Money
  OngoingOut: LoopOut list
  OngoingIn: LoopIn list
  FailedOut: ShortChannelId list
  FailedIn: NodeId list
  QuotesOut: QuoteOutRequestResponse
  QuotesIn: QuoteInRequestResponse
  ExpectedOut: LoopOutRequestResponse
  ExpectedIn: LoopInRequestResponse
}
  with
  static member Create(min: int64, max: int64) = {
    MinAmount = min |> Money.Satoshis
    MaxAmount = max |> Money.Satoshis
    OngoingOut = []
    OngoingIn = []
    FailedOut = []
    FailedIn = []
    QuotesOut = []
    QuotesIn = []
    ExpectedOut = []
    ExpectedIn = []
  }

[<RequireQualifiedAccess>]
module private AutoLoopManagerTests =
  ()
type AutoLoopManagerTests() =
  let offChain = SupportedCryptoCode.BTC
  let onChain = SupportedCryptoCode.BTC
  let loopOutPair = PairId(onChain, offChain)
  let loopInPair = PairId(offChain, onChain)
  member private this.TestAutoLoopManager(step: AutoLoopStep,
                                          parameters,
                                          channels,
                                          ?injection: IServiceCollection -> unit) = unitTask {
    let mutable loopOutQuoteCount = 0
    let mutable loopInQuoteCount = 0
    let mutable loopOutCount = 0
    let mutable loopInCount = 0
    let configureServices = fun (services: IServiceCollection) ->
      let dummySwapServerClient =
        TestHelpers.GetDummySwapServerClient
          {
            DummySwapServerClientParameters.Default
              with
              LoopOutQuote = fun req ->
                let expectedRequest, resp = step.QuotesOut.[loopOutQuoteCount]
                Assert.Equal(expectedRequest, req)
                loopOutQuoteCount <- loopOutQuoteCount + 1
                resp
              LoopInQuote = fun req ->
                let expectedRequest, resp = step.QuotesIn.[loopInQuoteCount]
                Assert.Equal(expectedRequest, req)
                loopInQuoteCount <- loopInQuoteCount + 1
                resp
              LoopOutTerms = fun _ ->
                { SwapDTO.OutTermsResponse.MaxSwapAmount = step.MaxAmount
                  MinSwapAmount = step.MinAmount }
              LoopInTerms = fun _ ->
                { SwapDTO.InTermsResponse.MaxSwapAmount = step.MaxAmount
                  MinSwapAmount = step.MinAmount }
          }
      let dummyLnClientProvider =
        TestHelpers.GetDummyLightningClientProvider
          {
            DummyLnClientParameters.Default with
              ListChannels = channels
          }
      let f = {
        new IFeeEstimator
          with
          member this.Estimate _target _cc =
            pairId.DefaultLoopOutParameters.SweepFeeRateLimit
            |> Task.FromResult
      }
      let mockBlockchainListener = {
        new IBlockChainListener with
          member this.CurrentHeight cc = BlockHeight.One
      }
      let mockOnGoingSwapProjection = {
        new IOnGoingSwapStateProjection with
          member this.State =
            Map.ofSeq[
              yield!
                step.OngoingOut
                |> Seq.map(fun t ->
                  let h = BlockHeight.Zero
                  ((StreamId.Create "swap-" (Guid.NewGuid())), (h, Swap.State.Out(h, t)))
                )
              yield!
                step.OngoingIn
                |> Seq.map(fun t ->
                  let h = BlockHeight.Zero
                  ((StreamId.Create "swap-" (Guid.NewGuid())), (h, Swap.State.In(h, t)))
                )
            ]
          member this.FinishCatchup = Task.CompletedTask
      }
      let mockRecentSwapFailureProjection = {
        new IRecentSwapFailureProjection with
          member this.FailedLoopIns =
            step.FailedIn
            |> List.map(fun x -> (x, testTime))
            |> Map.ofSeq
          member this.FailedLoopOuts =
            step.FailedOut
            |> List.map(fun x -> (x, testTime))
            |> Map.ofSeq
      }
      let swapExecutor =
        {
          new ISwapExecutor with
            member this.ExecNewLoopOut(req, h, s, ct) =
              let expectedReq, resp = step.ExpectedOut.[loopOutCount]
              loopOutCount <- loopOutCount + 1
              Assert.Equal(expectedReq, req)
              resp |> Ok |> Task.FromResult
            member this.ExecNewLoopIn(req, h, s, ct) =
              let expectedReq, resp = step.ExpectedIn.[loopInCount]
              loopInCount <- loopInCount + 1
              Assert.Equal(expectedReq, req)
              resp |> Ok |> Task.FromResult
        }
      services
        .AddSingleton<IFeeEstimator>(f)
        .AddSingleton<ISwapServerClient>(dummySwapServerClient)
        .AddSingleton<ISystemClock>({ new ISystemClock with member this.UtcNow = testTime })
        .AddSingleton<ISwapExecutor>(swapExecutor)
        .AddSingleton<IBlockChainListener>(mockBlockchainListener)
        .AddSingleton<IOnGoingSwapStateProjection>(mockOnGoingSwapProjection)
        .AddSingleton<IRecentSwapFailureProjection>(mockRecentSwapFailureProjection)
        .AddSingleton<ILightningClientProvider>(dummyLnClientProvider)
        |> ignore
      injection
      |> Option.iter(fun inject -> inject services)
    use server = new TestServer(TestHelpers.GetTestHost configureServices)
    let getManager = server.Services.GetService<TryGetAutoLoopManager>()
    Assert.NotNull(getManager)
    let man = (getManager offChain).Value
    let! r = man.SetParameters parameters
    Assertion.isOk r

    use cts = new CancellationTokenSource()
    cts.CancelAfter(3000)
    do! man.RunStep(cts.Token)
    Assert.Equal(step.QuotesOut.Length, loopOutQuoteCount)
    Assert.Equal(step.QuotesIn.Length, loopInQuoteCount)
    Assert.Equal(step.ExpectedOut.Length, loopOutCount)
    Assert.Equal(step.ExpectedIn.Length, loopInCount)
  }

  /// Tests the case where we need to perform a swap, but autoloop is not enabled.
  [<Fact>]
  member this.TestAutoLoopDisabled() = unitTask {
    let channels: ListChannelResponse list = [channel1]
    let parameters = {
      Parameters.Default onChain
        with
        Rules = { Rules.Zero with ChannelRules =  Map.ofSeq[(chanId1, chanRule)] }
    }

    let step = {
      AutoLoopStep.Create(min=1L, max=chan1Rec.Amount.Satoshi + 1L)
        with
        QuotesOut =
          let req = {
            SwapDTO.LoopOutQuoteRequest.Amount = chan1Rec.Amount
            SwapDTO.LoopOutQuoteRequest.SweepConfTarget = pairId.DefaultLoopOutParameters.SweepConfTarget
            SwapDTO.LoopOutQuoteRequest.Pair = pairId
          }
          [(req, testQuote)]
    }
    do! this.TestAutoLoopManager(step, parameters, channels)
    // Trigger another autoloop, this time setting our server restrictions
    // To have a minimum swap amount greater than the amount that we need to swap.
    // In this case we don't even expect to get a quote, because
    // our suggested swap is beneath the minimum swap size.
    let step = AutoLoopStep.Create(min=chan1Rec.Amount.Satoshi + 1L, max=chan1Rec.Amount.Satoshi)
    do! this.TestAutoLoopManager(step, parameters, channels)
  }

  [<Fact>]
  member this.TestAutoLoopEnabled() = unitTask {
    let swapFeePPM = 1000L<ppm>
    let routeFeePPM = 1000L<ppm>
    let prepayFeePPM = 1000L<ppm>
    let prepayAmount = 20000L |> Money.Satoshis
    let maxMiner = 20000L |> Money.Satoshis
    let parameters = {
      AutoLoop = true
      MaxAutoInFlight = 2
      FailureBackoff = TimeSpan.FromHours(1.)
      SweepConfTarget = BlockHeightOffset32(2u)
      FeeLimit = {
        FeeCategoryLimit.MaximumSwapFeePPM = swapFeePPM
        MaximumPrepay = prepayAmount
        MaximumRoutingFeePPM = routeFeePPM
        MaximumPrepayRoutingFeePPM = prepayFeePPM
        MaximumMinerFee = maxMiner
        SweepFeeRateLimit = FeeRate(80m)
      }
      ClientRestrictions = ClientRestrictions.Default
      Rules = { Rules.Zero with ChannelRules = Map.ofSeq[(chanId1, chanRule); (chanId2, chanRule)] }
      HTLCConfTarget = pairId.DefaultLoopInParameters.HTLCConfTarget
      OnChainAsset = onChain }

    let channels = [channel1; channel2]
    let amt = chan1Rec.Amount
    let maxSwapFee = ppmToSat(amt, swapFeePPM)
    let quoteRequest = {
      SwapDTO.LoopOutQuoteRequest.Amount = amt
      SwapDTO.LoopOutQuoteRequest.SweepConfTarget = parameters.SweepConfTarget
      SwapDTO.LoopOutQuoteRequest.Pair = loopOutPair
    }
    let quote1 = {
      SwapDTO.LoopOutQuote.SwapFee = maxSwapFee
      SwapDTO.LoopOutQuote.SweepMinerFee = maxSwapFee - (10L |> Money.Satoshis)
      SwapDTO.LoopOutQuote.SwapPaymentDest = (new Key()).PubKey
      SwapDTO.LoopOutQuote.CltvDelta = BlockHeightOffset32(10u)
      SwapDTO.LoopOutQuote.PrepayAmount = prepayAmount - (10L |> Money.Satoshis)
    }
    let quote2 = {
      SwapDTO.LoopOutQuote.SwapFee = maxSwapFee
      SwapDTO.LoopOutQuote.SweepMinerFee = maxSwapFee - (10L |> Money.Satoshis)
      SwapDTO.LoopOutQuote.SwapPaymentDest = (new Key()).PubKey
      SwapDTO.LoopOutQuote.CltvDelta = BlockHeightOffset32(10u)
      SwapDTO.LoopOutQuote.PrepayAmount = prepayAmount - (20L |> Money.Satoshis)
    }
    let quotes: QuoteOutRequestResponse =
      [
        (quoteRequest, quote1)
        (quoteRequest, quote2)
      ]

    let maxRouteFee = ppmToSat(amt, routeFeePPM)
    let chan1Swap = {
      LoopOutRequest.Amount = amt
      ChannelIds = [|chanId1|] |> ValueSome
      Address = Helpers.lndAddress |> Some
      PairId = loopOutPair |> Some
      SwapTxConfRequirement =
        loopOutPair.DefaultLoopOutParameters.SwapTxConfRequirement.Value |> int |> Some
      Label =
        Labels.autoLoopLabel(Swap.Category.Out) |> Some
      MaxSwapRoutingFee = maxRouteFee |> ValueSome
      MaxPrepayRoutingFee = ppmToSat(quote1.PrepayAmount, routeFeePPM) |> ValueSome
      MaxSwapFee = quote1.SwapFee |> ValueSome
      MaxPrepayAmount = quote1.PrepayAmount |> ValueSome
      MaxMinerFee = maxMiner |> ValueSome
      SweepConfTarget = parameters.SweepConfTarget.Value |> int |> ValueSome
    }
    let chan2Swap = {
      LoopOutRequest.Amount = amt
      ChannelIds = [|chanId2|] |> ValueSome
      Address = Helpers.lndAddress |> Some
      PairId = loopOutPair |> Some
      SwapTxConfRequirement =
        loopOutPair.DefaultLoopOutParameters.SwapTxConfRequirement.Value |> int |> Some
      Label =
        Labels.autoLoopLabel(Swap.Category.Out) |> Some
      MaxSwapRoutingFee = maxRouteFee |> ValueSome
      MaxPrepayRoutingFee = ppmToSat(quote2.PrepayAmount, routeFeePPM) |> ValueSome
      MaxSwapFee = quote2.SwapFee |> ValueSome
      MaxPrepayAmount = quote2.PrepayAmount |> ValueSome
      MaxMinerFee = maxMiner |> ValueSome
      SweepConfTarget = parameters.SweepConfTarget.Value |> int |> ValueSome
    }
    let loopOuts: LoopOutRequestResponse =
      let resp1 = {
        LoopOutResponse.Address = "resp1-address"
        Id = "resp1"
        ClaimTxId = None
      }
      let resp2 = {
        LoopOutResponse.Address = "resp2-address"
        Id = "resp2"
        ClaimTxId = None
      }
      [
        (chan1Swap, resp1)
        (chan2Swap, resp2)
      ]
    let step = {
      AutoLoopStep.Create(1L, amt.Satoshi + 1L)
        with
          ExpectedOut = loopOuts
          QuotesOut = quotes
    }
    do! this.TestAutoLoopManager(step, parameters, channels)

    ()
  }
