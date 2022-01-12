namespace NLoop.Server.Tests

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open DotNetLightning.Utils
open LndClient
open FSharp.Control.Tasks
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Internal
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

type private AutoLoopManagerTestContext() =
  let offChain = SupportedCryptoCode.BTC
  let onChain = SupportedCryptoCode.BTC
  let loopOutPair = PairId(onChain, offChain)
  let loopInPair = PairId(offChain, onChain)
  member val Manager: AutoLoopManager = Unchecked.defaultof<_> with get, set

  member val QuotesOutChannel = Channel.CreateUnbounded<SwapDTO.LoopOutQuoteRequest * SwapDTO.LoopOutQuote>()
  member val QuotesInChannel = Channel.CreateUnbounded<SwapDTO.LoopInQuoteRequest * SwapDTO.LoopInQuote>()
  member val ExpectedOutChannel = Channel.CreateUnbounded<LoopOutRequest * LoopOutResponse>()
  member val ExpectedInChannel = Channel.CreateUnbounded<LoopInRequest * LoopInResponse>()

  member val MinAmount = Money.Zero with get, set
  member val MaxAmount = Money.Zero with get, set

  member val OngoingOut: LoopOut list = [] with get, set
  member val OngoingIn: LoopIn list = [] with get, set
  member val FailedOut: ShortChannelId list = [] with get, set
  member val FailedIn: NodeId list = [] with get, set

  member val TestTime = testTime with get, set

  member this.Prepare(parameters, channels) = task {
    let configureServices = fun (services: IServiceCollection) ->
      let dummySwapServerClient =
        TestHelpers.GetDummySwapServerClient
          {
            DummySwapServerClientParameters.Default
              with
              LoopOutQuote = fun req -> task {
                use cts = new CancellationTokenSource()
                cts.CancelAfter(10)
                let! expectedRequest, resp = this.QuotesOutChannel.Reader.ReadAsync(cts.Token)
                Assert.Equal(expectedRequest, req)
                return resp
              }
              LoopInQuote = fun req -> task {
                use cts = new CancellationTokenSource()
                cts.CancelAfter(10)
                let! expectedRequest, resp = this.QuotesInChannel.Reader.ReadAsync(cts.Token)
                Assert.Equal(expectedRequest, req)
                return resp
              }
              LoopOutTerms = fun _ -> task {
                return
                  { SwapDTO.OutTermsResponse.MaxSwapAmount = this.MaxAmount
                    MinSwapAmount = this.MinAmount }
              }
              LoopInTerms = fun _ -> task {
                return
                  { SwapDTO.InTermsResponse.MaxSwapAmount = this.MaxAmount
                    MinSwapAmount = this.MinAmount }
              }
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
          member _.State =
            Map.ofSeq[
              yield!
                this.OngoingOut
                |> Seq.map(fun t ->
                  let h = BlockHeight.Zero
                  ((StreamId.Create "swap-" (Guid.NewGuid())), (h, Swap.State.Out(h, t)))
                )
              yield!
                this.OngoingIn
                |> Seq.map(fun t ->
                  let h = BlockHeight.Zero
                  ((StreamId.Create "swap-" (Guid.NewGuid())), (h, Swap.State.In(h, t)))
                )
            ]
          member this.FinishCatchup = Task.CompletedTask
      }
      let mockRecentSwapFailureProjection = {
        new IRecentSwapFailureProjection with
          member _.FailedLoopIns =
            this.FailedIn
            |> List.map(fun x -> (x, testTime))
            |> Map.ofSeq
          member _.FailedLoopOuts =
            this.FailedOut
            |> List.map(fun x -> (x, testTime))
            |> Map.ofSeq
      }
      let swapExecutor =
        {
          new ISwapExecutor with
            member exe.ExecNewLoopOut(req, h, s, ct) = task {
              let ct = defaultArg ct CancellationToken.None
              use cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
              cts.CancelAfter(10)
              let! expectedReq, resp = this.ExpectedOutChannel.Reader.ReadAsync(cts.Token)
              Assert.Equal(expectedReq, req)
              return resp |> Ok
            }
            member exe.ExecNewLoopIn(req, h, s, ct) = task {
              let ct = defaultArg ct CancellationToken.None
              use cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
              cts.CancelAfter(10)
              let! expectedReq, resp = this.ExpectedInChannel.Reader.ReadAsync(cts.Token)
              Assert.Equal(expectedReq, req)
              return resp |> Ok
            }
        }
      services
        .AddSingleton<IFeeEstimator>(f)
        .AddSingleton<ISwapServerClient>(dummySwapServerClient)
        .AddSingleton<ISystemClock>({ new ISystemClock with member _.UtcNow = this.TestTime })
        .AddSingleton<ISwapExecutor>(swapExecutor)
        .AddSingleton<IBlockChainListener>(mockBlockchainListener)
        .AddSingleton<IOnGoingSwapStateProjection>(mockOnGoingSwapProjection)
        .AddSingleton<IRecentSwapFailureProjection>(mockRecentSwapFailureProjection)
        .AddSingleton<ILightningClientProvider>(dummyLnClientProvider)
        |> ignore

    let sp = TestHelpers.GetTestServiceProvider(configureServices)
    let getManager = sp.GetService<TryGetAutoLoopManager>()
    Assert.NotNull(getManager)
    let man = (getManager offChain).Value
    let! r = man.SetParameters parameters
    Assertion.isOk r
    this.Manager <- man
  }

  member this.RunStep(step: AutoLoopStep) = task {
    use cts = new CancellationTokenSource()
    cts.CancelAfter(3000)
    for e in step.ExpectedOut do
      do! this.ExpectedOutChannel.Writer.WriteAsync(e, cts.Token)
    for e in step.ExpectedIn do
      do! this.ExpectedInChannel.Writer.WriteAsync(e, cts.Token)
    for e in step.QuotesOut do
      do! this.QuotesOutChannel.Writer.WriteAsync(e, cts.Token)
    for e in step.QuotesIn do
      do! this.QuotesInChannel.Writer.WriteAsync(e, cts.Token)

    this.MinAmount <- step.MinAmount
    this.MaxAmount <- step.MaxAmount
    this.FailedOut <- step.FailedOut
    this.FailedIn <- step.FailedIn
    this.OngoingOut <- step.OngoingOut
    this.OngoingIn <- step.OngoingIn

    do! this.Manager.RunStep(cts.Token)

    Assert.Equal(0, this.ExpectedOutChannel.Reader.Count)
    Assert.Equal(0, this.ExpectedInChannel.Reader.Count)
    Assert.Equal(0, this.QuotesOutChannel.Reader.Count)
    Assert.Equal(0, this.QuotesInChannel.Reader.Count)
    ()
  }

  interface IDisposable with
    member this.Dispose() =
      if this.Manager |> box |> isNull |> not then
        this.Manager.Dispose()

type AutoLoopManagerTests() =

  let offChain = SupportedCryptoCode.BTC
  let onChain = SupportedCryptoCode.BTC
  let loopOutPair = PairId(onChain, offChain)
  let loopInPair = PairId(offChain, onChain)
  let dummyResp =
    {
      LoopOutResponse.Address = "resp1-address"
      Id = "resp1"
      ClaimTxId = None
    }
  let ongoingLoopOutFromRequest(req: LoopOutRequest, initTime: DateTimeOffset) =
    {
      LoopOut.OnChainAmount = req.Amount
      Id = SwapId swapId
      OutgoingChanIds = req.OutgoingChannelIds
      SwapTxConfRequirement =
        req.Limits.SwapTxConfRequirement
      ClaimKey = claimKey
      Preimage = preimage
      RedeemScript = Script()
      Invoice = ""
      ClaimAddress = ""
      TimeoutBlockHeight = BlockHeight(30u)
      SwapTxHex = None
      SwapTxHeight = None
      ClaimTransactionId = None
      IsClaimTxConfirmed = false
      IsOffchainOfferResolved = false
      PairId = req.PairIdValue
      Label = req.Label |> Option.defaultValue ""
      PrepayInvoice = ""
      SweepConfTarget =
        req.SweepConfTarget
        |> ValueOption.map(uint >> BlockHeightOffset32)
        |> ValueOption.defaultWith(fun x -> failwith $"{x}")
      MaxMinerFee = req.Limits.MaxMinerFee
      ChainName = Network.RegTest.ChainName.ToString()
      Cost = SwapCost.Zero
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

    use ctx = new AutoLoopManagerTestContext()
    do! ctx.Prepare(parameters, channels)

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
    do! ctx.RunStep(step)

    // Trigger another autoloop, this time setting our server restrictions
    // To have a minimum swap amount greater than the amount that we need to swap.
    // In this case we don't even expect to get a quote, because
    // our suggested swap is beneath the minimum swap size.
    let step = AutoLoopStep.Create(min=chan1Rec.Amount.Satoshi + 1L, max=chan1Rec.Amount.Satoshi)
    do! ctx.RunStep(step)
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
      [
        (chan1Swap, dummyResp)
        (chan2Swap, dummyResp)
      ]

    use ctx = new AutoLoopManagerTestContext()
    do! ctx.Prepare(parameters, channels)
    let step = {
      AutoLoopStep.Create(1L, amt.Satoshi + 1L)
        with
          ExpectedOut = loopOuts
          QuotesOut = quotes
    }
    do! ctx.RunStep(step)

    let existing = [
      ongoingLoopOutFromRequest(chan1Swap, testTime)
      ongoingLoopOutFromRequest(chan2Swap, testTime)
    ]
    let step = {
      AutoLoopStep.Create(1L, amt.Satoshi + 1L)
        with
          OngoingOut = existing
    }
    do! ctx.RunStep(step)

    // case 2: channel 2 swap now has previous off-chain failure.
    let step = {
      AutoLoopStep.Create(1L, amt.Satoshi + 1L)
        with
        FailedOut = [chanId2]
        QuotesOut = [(quoteRequest, quote1)]
        ExpectedOut = [(chan1Swap, dummyResp)]
    }
    do! ctx.RunStep(step)
    // but if we wait enough...
    ctx.TestTime <- testTime + parameters.FailureBackoff + TimeSpan.FromSeconds(1.)

    // swap will happen again.
    let step = {
      step
        with
        QuotesOut = [(quoteRequest, quote1); (quoteRequest, quote2)]
        ExpectedOut = [(chan1Swap, dummyResp); (chan2Swap, dummyResp)]
    }
    do! ctx.RunStep(step)
    ()
  }


  [<Fact>]
  member this.TestCompositeRules() =
    ()
