namespace NLoop.Server.Tests

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open DotNetLightning.Utils
open LndClient
open FSharp.Control.Tasks
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

    Assert.Equal(0, this.QuotesOutChannel.Reader.Count)
    Assert.Equal(0, this.QuotesInChannel.Reader.Count)
    Assert.Equal(0, this.ExpectedOutChannel.Reader.Count)
    Assert.Equal(0, this.ExpectedInChannel.Reader.Count)
    ()
  }

  interface IDisposable with
    member this.Dispose() =
      if this.Manager |> box |> isNull |> not then
        this.Manager.Dispose()

type AutoLoopManagerTests() =

  let offChainAsset = SupportedCryptoCode.BTC
  let onChainAsset = SupportedCryptoCode.BTC
  let loopOutPair = PairId(onChainAsset, offChainAsset)
  let loopInPair = PairId(offChainAsset, onChainAsset)
  let dummyOutResp =
    {
      LoopOutResponse.Address = "resp1-address"
      Id = "resp1"
      ClaimTxId = None
    }

  let dummyInResp = {
    LoopInResponse.Id = "resp1"
    Address = "resp1-address"
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

  let ongoingLoopInFromRequest(req: LoopInRequest, initTime: DateTimeOffset) =
    {
      LoopIn.Id = SwapId swapId
      RefundPrivateKey = refundKey
      Preimage = None
      RedeemScript = swapRedeem
      Invoice = invoice.ToString()
      Address = Helpers.lndAddress.ToString()
      ExpectedAmount = req.Amount
      TimeoutBlockHeight = BlockHeight(32u)
      HTLCConfTarget =
        req.HtlcConfTarget
        |> ValueOption.map(uint >> BlockHeightOffset32)
        |> ValueOption.defaultValue loopInPair.DefaultLoopInParameters.HTLCConfTarget
      SwapTxInfoHex = None
      RefundTransactionId = None
      PairId = loopInPair
      Label = Labels.autoIn
      ChainName = Network.RegTest.ChainName.ToString()
      MaxMinerFee = req.Limits.MaxMinerFee
      MaxSwapFee = req.Limits.MaxSwapFee
      LastHop = req.LastHop
      IsOffChainPaymentReceived = false
      IsOurSuccessTxConfirmed = false
      Cost = SwapCost.Zero
    }

  /// Tests the case where we need to perform a swap, but autoloop is not enabled.
  [<Fact>]
  member this.TestAutoLoopDisabled() = unitTask {
    let channels: ListChannelResponse list = [channel1]
    let parameters = {
      Parameters.Default onChainAsset
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
  member this.TestAutoLoopOutEnabled() = unitTask {
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
      ClientRestrictions = ClientRestrictions.NoRestriction
      Rules = { Rules.Zero with ChannelRules = Map.ofSeq[(chanId1, chanRule); (chanId2, chanRule)] }
      HTLCConfTarget = pairId.DefaultLoopInParameters.HTLCConfTarget
      OnChainAsset = onChainAsset }

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
    let maxRouteFee = ppmToSat(amt, routeFeePPM)
    let chan1Swap = {
      LoopOutRequest.Amount = amt
      ChannelIds = [|chanId1|] |> ValueSome
      Address = Helpers.lndAddress.ToString() |> Some
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
      Address = Helpers.lndAddress.ToString() |> Some
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
    use ctx = new AutoLoopManagerTestContext()
    do! ctx.Prepare(parameters, channels)
    let step = {
      AutoLoopStep.Create(1L, amt.Satoshi + 1L)
        with
          QuotesOut =
            [
              (quoteRequest, quote1)
              (quoteRequest, quote2)
            ]
          ExpectedOut =
            [
              (chan1Swap, dummyOutResp)
              (chan2Swap, dummyOutResp)
            ]
    }
    do! ctx.RunStep(step)

    let step = {
      AutoLoopStep.Create(1L, amt.Satoshi + 1L)
        with
          OngoingOut =
            [
              ongoingLoopOutFromRequest(chan1Swap, testTime)
              ongoingLoopOutFromRequest(chan2Swap, testTime)
            ]
    }
    do! ctx.RunStep(step)

    // case 2: channel 2 swap now has previous off-chain failure.
    let step = {
      AutoLoopStep.Create(1L, amt.Satoshi + 1L)
        with
        FailedOut = [chanId2]
        QuotesOut = [(quoteRequest, quote1)]
        ExpectedOut = [(chan1Swap, dummyOutResp)]
    }
    do! ctx.RunStep(step)

    // but if we wait enough...
    ctx.TestTime <- testTime + parameters.FailureBackoff + TimeSpan.FromSeconds(1.)
    // swap will happen again.
    let step = {
      step
        with
        QuotesOut = [(quoteRequest, quote1); (quoteRequest, quote2)]
        ExpectedOut = [(chan1Swap, dummyOutResp); (chan2Swap, dummyOutResp)]
    }
    do! ctx.RunStep(step)
  }


  [<Fact>]
  member this.TestCompositeRules() = task {
    // set up another channels so that we have two channels with peer2.
    // and a single channel with peer1.
    let channel3 = {
      ListChannelResponse.Cap = 10000L |> Money.Satoshis
      Id = chanId3
      LocalBalance = 10000L |> Money.Satoshis
      NodeId = peer2
    }

    let channels = [channel1; channel2; channel3]
    let swapFeePPM = 1000L<ppm>
    let routeFeePPM = 1000L<ppm>
    let prepayFeePPM = 1000L<ppm>
    let prepayAmount = 20000L |> Money.Satoshis
    let maxMiner = 20000L |> Money.Satoshis

    let parameters = {
      Parameters.FeeLimit = {
        FeeCategoryLimit.MaximumPrepay = prepayAmount
        MaximumSwapFeePPM = swapFeePPM
        MaximumRoutingFeePPM = routeFeePPM
        MaximumPrepayRoutingFeePPM = prepayFeePPM
        MaximumMinerFee = maxMiner
        SweepFeeRateLimit = FeeRate(satoshiPerByte=80m) }
      MaxAutoInFlight = 2
      FailureBackoff = TimeSpan.FromHours(1.)
      SweepConfTarget = BlockHeightOffset32(10u)
      ClientRestrictions = ClientRestrictions.NoRestriction
      Rules = {
        ChannelRules = Map.ofSeq [(chanId1, chanRule)]
        PeerRules = Map.ofSeq[(peer2 |> NodeId, chanRule)]
      }
      HTLCConfTarget = onChainAsset.DefaultParams.OnChain.HTLCConfTarget
      AutoLoop = true
      OnChainAsset = onChainAsset }

    use ctx = new AutoLoopManagerTestContext()
    do! ctx.Prepare(parameters, channels)

    // calculates our maximum allowed fees and create quotes that fall within our budget
    let peerAmount = 15000L |> Money.Satoshis
    let maxPeerSwapFee = ppmToSat(peerAmount, swapFeePPM)
    let peerSwapQuote = {
      SwapDTO.LoopOutQuote.SwapFee = maxPeerSwapFee
      SwapDTO.LoopOutQuote.SweepMinerFee = maxMiner - Money.Satoshis(10L)
      SwapDTO.LoopOutQuote.SwapPaymentDest = (new Key()).PubKey
      SwapDTO.LoopOutQuote.CltvDelta = BlockHeightOffset32(10u)
      SwapDTO.LoopOutQuote.PrepayAmount = prepayAmount - Money.Satoshis 20L
    }
    let peerSwapQuoteRequest = {
      SwapDTO.LoopOutQuoteRequest.Amount = peerAmount
      SwapDTO.LoopOutQuoteRequest.SweepConfTarget = parameters.SweepConfTarget
      SwapDTO.LoopOutQuoteRequest.Pair =  loopOutPair
    }
    let maxPeerRouteFee = ppmToSat(peerAmount, routeFeePPM)
    let peerSwap = {
      LoopOutRequest.Amount = peerAmount
      ChannelIds = [| chanId2; chanId3 |] |> ValueSome
      Address = Helpers.lndAddress.ToString() |> Some
      PairId = loopOutPair |> Some
      SwapTxConfRequirement =
        loopOutPair.DefaultLoopOutParameters.SwapTxConfRequirement.Value |> int |> Some
      Label = Labels.autoOut |> Some
      MaxSwapRoutingFee = maxPeerRouteFee |> ValueSome
      MaxPrepayRoutingFee = ppmToSat(peerSwapQuote.PrepayAmount, routeFeePPM) |> ValueSome
      MaxSwapFee = peerSwapQuote.SwapFee |> ValueSome
      MaxPrepayAmount = peerSwapQuote.PrepayAmount |> ValueSome
      MaxMinerFee = maxMiner |> ValueSome
      SweepConfTarget = parameters.SweepConfTarget.Value |> int |> ValueSome
    }
    let chanAmount = chan1Rec.Amount
    let channelSwapQuote = {
      SwapDTO.LoopOutQuote.SwapFee = ppmToSat(chanAmount, swapFeePPM)
      SwapDTO.LoopOutQuote.SweepMinerFee = maxMiner - Money.Satoshis(10L)
      SwapDTO.LoopOutQuote.SwapPaymentDest = (new Key()).PubKey
      SwapDTO.LoopOutQuote.CltvDelta = BlockHeightOffset32(10u)
      SwapDTO.LoopOutQuote.PrepayAmount = prepayAmount - Money.Satoshis 10L
    }
    let chanSwapQuoteRequest = {
      peerSwapQuoteRequest
        with
          SwapDTO.LoopOutQuoteRequest.Amount = chanAmount
    }
    let chanSwap = {
      peerSwap
        with
          LoopOutRequest.Amount = chanAmount
          ChannelIds = [| chanId1 |] |> ValueSome
          MaxSwapFee = channelSwapQuote.SwapFee |> ValueSome
          MaxSwapRoutingFee = ppmToSat(chanAmount, routeFeePPM) |> ValueSome
          MaxPrepayAmount = channelSwapQuote.PrepayAmount |> ValueSome
          MaxPrepayRoutingFee = ppmToSat(channelSwapQuote.PrepayAmount, routeFeePPM) |> ValueSome
    }
    let step = {
      AutoLoopStep.Create(min = 1L, max = peerAmount.Satoshi + 1L)
        with
        QuotesOut = [(peerSwapQuoteRequest, peerSwapQuote); (chanSwapQuoteRequest, channelSwapQuote); ]
        ExpectedOut = [(chanSwap, dummyOutResp); (peerSwap, dummyOutResp); ]
    }
    do! ctx.RunStep(step)
  }

  [<Fact>]
  member this.TestAutoLoopInEnabled() = task {
    let chan1 = {
      ListChannelResponse.Id = chanId1
      Cap = 100000L |> Money.Satoshis
      LocalBalance = Money.Zero
      NodeId = peer1
    }
    let chan2 = {
      ListChannelResponse.Id = chanId2
      Cap = 200000L |> Money.Satoshis
      LocalBalance = Money.Zero
      NodeId = peer2
    }

    let channels = [chan1; chan2]
    // create a rule which will loop in, with no inbound liquidity reserve.
    let rule =  {
      MinimumIncoming = 0s<percent>
      MinimumOutGoing = 60s<percent>
    }
    let peer1ExpectedAmount = 80000L |> Money.Satoshis
    let peer2ExpectedAmount = 160000L |> Money.Satoshis
    let swapFeePPM = 50000L<ppm>
    let htlcConfTarget = BlockHeightOffset32 10u
    let peer1MaxFee = ppmToSat(peer1ExpectedAmount, swapFeePPM)
    let peer2MaxFee = ppmToSat(peer2ExpectedAmount, swapFeePPM)
    let parameters = {
      Parameters.OnChainAsset = onChainAsset
      MaxAutoInFlight = 2
      FailureBackoff = TimeSpan.FromHours(1.)
      SweepConfTarget = onChainAsset.DefaultParams.OnChain.SweepConfTarget
      FeeLimit = { FeePortion.PartsPerMillion = swapFeePPM }
      ClientRestrictions = ClientRestrictions.NoRestriction
      Rules = {
        PeerRules = Map.ofSeq[(peer1 |> NodeId, rule); (peer2 |> NodeId, rule)]
        ChannelRules = Map.empty
      }
      HTLCConfTarget = htlcConfTarget
      AutoLoop = true }

    use ctx = new AutoLoopManagerTestContext()
    do! ctx.Prepare(parameters, channels)
    let quote1 = {
      SwapDTO.LoopInQuote.SwapFee =
        (peer1MaxFee.Satoshi / 4L) |> Money.Satoshis
      SwapDTO.LoopInQuote.MinerFee =
        (peer1MaxFee.Satoshi / 8L) |> Money.Satoshis
    }
    let quote2Unaffordable = {
        SwapDTO.LoopInQuote.SwapFee =
          peer2MaxFee * 2L
        SwapDTO.LoopInQuote.MinerFee =
          peer2MaxFee * 2L
    }
    let quoteRequest1 = {
      SwapDTO.LoopInQuoteRequest.Amount = peer1ExpectedAmount
      SwapDTO.LoopInQuoteRequest.Pair = loopInPair }
    let quoteRequest2 = {
      SwapDTO.LoopInQuoteRequest.Amount = peer2ExpectedAmount
      SwapDTO.LoopInQuoteRequest.Pair = loopInPair }
    let peer1Swap = {
      LoopInRequest.Amount = peer1ExpectedAmount
      ChannelId = None
      LastHop = peer1 |> Some
      Label = Labels.autoIn |> Some
      PairId = pairId |> Some
      MaxMinerFee = quote1.MinerFee |> ValueSome
      MaxSwapFee = quote1.SwapFee |> ValueSome
      HtlcConfTarget = htlcConfTarget.Value |> int |> ValueSome
    }

    let step = {
      AutoLoopStep.Create(min = 1L, max = peer2ExpectedAmount.Satoshi + 1L)
        with
        QuotesIn = [(quoteRequest1, quote1); (quoteRequest2, quote2Unaffordable)]
        ExpectedIn = [(peer1Swap, dummyInResp)]
    }
    do! ctx.RunStep(step)

    // Now, we tick again with our first swap in progress. This time, we
    // provide a quote for our second swap which is more affordable, so we
    // expect it to be dispatched

    let quote2Affordable = {
      SwapDTO.LoopInQuote.SwapFee =
        (peer2MaxFee.Satoshi / 8L) |> Money.Satoshis
      SwapDTO.LoopInQuote.MinerFee =
        (peer2MaxFee.Satoshi / 2L) |> Money.Satoshis
    }
    let peer2Swap = {
      LoopInRequest.Amount = peer2ExpectedAmount
      ChannelId = None
      LastHop = peer2 |> Some
      Label = Labels.autoIn |> Some
      PairId = pairId |> Some
      MaxMinerFee = quote2Affordable.MinerFee |> ValueSome
      MaxSwapFee = quote2Affordable.SwapFee |> ValueSome
      HtlcConfTarget = htlcConfTarget.Value |> int |> ValueSome
    }
    let step = {
      AutoLoopStep.Create(min = 1L, max = peer2ExpectedAmount.Satoshi + 1L)
        with
        QuotesIn = [(quoteRequest2, quote2Affordable)]
        OngoingIn = [ongoingLoopInFromRequest(peer1Swap, testTime)]
        ExpectedIn = [(peer2Swap, dummyInResp)]
    }
    do! ctx.RunStep(step)
  }

  [<Fact>]
  member this.TestAutoloopBothTypes() = task {
    let chan1 = {
      ListChannelResponse.Id = chanId1
      NodeId = peer1
      Cap =
        1000000L |> Money.Satoshis
      LocalBalance =
        1000000L |> Money.Satoshis
    }
    let chan2 = {
      ListChannelResponse.Id = chanId2
      NodeId = peer2
      Cap =
        200000L |> Money.Satoshis
      LocalBalance = Money.Zero
    }
    let channels = [chan1; chan2]
    let outRule =  {
      ThresholdRule.MinimumIncoming = 40s<percent>
      ThresholdRule.MinimumOutGoing = 0s<percent>
    }
    let inRule = {
      ThresholdRule.MinimumIncoming = 0s<percent>
      ThresholdRule.MinimumOutGoing = 60s<percent>
    }
    let loopOutAmount = 700000L |> Money.Satoshis
    let loopInAmount =  160000L |> Money.Satoshis
    let swapFeePPM = 50000L<ppm>
    let htlcConfTarget = BlockHeightOffset32 10u
    let loopOutMaxFee = ppmToSat(loopOutAmount, swapFeePPM)
    let loopInMaxFee = ppmToSat(loopInAmount, swapFeePPM)
    let parameters = {
      Parameters.AutoLoop = true
      MaxAutoInFlight = 2
      FailureBackoff = TimeSpan.FromHours(1.)
      SweepConfTarget = onChainAsset.DefaultParams.OnChain.SweepConfTarget
      FeeLimit = { FeePortion.PartsPerMillion =  swapFeePPM }
      ClientRestrictions = ClientRestrictions.NoRestriction
      Rules = {
        ChannelRules = Map.ofSeq[(chanId1, outRule)]
        PeerRules = Map.ofSeq[(peer2 |> NodeId, inRule)]
      }
      HTLCConfTarget = htlcConfTarget
      OnChainAsset = onChainAsset
    }

    use ctx = new AutoLoopManagerTestContext()
    do! ctx.Prepare(parameters, channels)
    let loopOutQuote =
      {
        SwapDTO.LoopOutQuote.SwapFee =
          (loopOutMaxFee.Satoshi / 4L) |> Money.Satoshis
        SwapDTO.LoopOutQuote.SweepMinerFee = Money.Zero
        SwapDTO.LoopOutQuote.SwapPaymentDest = (new Key()).PubKey
        SwapDTO.LoopOutQuote.CltvDelta = BlockHeightOffset32(10u)
        SwapDTO.LoopOutQuote.PrepayAmount =
          (loopOutMaxFee.Satoshi / 4L) |> Money.Satoshis
      }
    let loopOutQuoteReq =
      {
        SwapDTO.LoopOutQuoteRequest.Amount = loopOutAmount
        SwapDTO.LoopOutQuoteRequest.SweepConfTarget =
          parameters.SweepConfTarget
        SwapDTO.LoopOutQuoteRequest.Pair = loopOutPair
      }
    let prepayMaxFee, routeMaxFee, minerFee = parameters.FeeLimit.LoopOutFees(loopOutAmount, loopOutQuote)
    let loopOutSwap = {
      LoopOutRequest.Amount = loopOutAmount
      ChannelIds = [|chanId1|] |> ValueSome
      Address = Some(Helpers.lndAddress.ToString())
      PairId = loopOutPair |> Some
      SwapTxConfRequirement =
        loopOutPair.DefaultLoopOutParameters.SwapTxConfRequirement.Value |> int |> Some
      Label = Labels.autoOut |> Some
      MaxSwapRoutingFee = routeMaxFee |> ValueSome
      MaxPrepayRoutingFee = prepayMaxFee |> ValueSome
      MaxSwapFee = loopOutQuote.SwapFee |> ValueSome
      MaxPrepayAmount = loopOutQuote.PrepayAmount |> ValueSome
      MaxMinerFee = minerFee |> ValueSome
      SweepConfTarget = parameters.SweepConfTarget.Value |> int |> ValueSome
    }

    let loopInQuote = {
      SwapDTO.LoopInQuote.SwapFee =
        (loopInMaxFee.Satoshi / 4L)
        |> Money.Satoshis
      SwapDTO.LoopInQuote.MinerFee =
        (loopInMaxFee.Satoshi / 8L)
        |> Money.Satoshis
    }
    let loopInQuoteReq = {
      SwapDTO.LoopInQuoteRequest.Amount = loopInAmount
      SwapDTO.LoopInQuoteRequest.Pair = loopInPair
    }
    let loopInSwap =
      {
        LoopInRequest.Amount = loopInAmount
        ChannelId = None
        LastHop = peer2 |> Some
        Label = Labels.autoIn |> Some
        PairId = loopInPair |> Some
        MaxMinerFee = loopInQuote.MinerFee |> ValueSome
        MaxSwapFee = loopInQuote.SwapFee |> ValueSome
        HtlcConfTarget = htlcConfTarget.Value |> int |> ValueSome
      }

    let step = {
      AutoLoopStep.Create(1L, loopOutAmount.Satoshi + 1L)
        with
        QuotesOut = [(loopOutQuoteReq, loopOutQuote)]
        QuotesIn = [(loopInQuoteReq, loopInQuote)]
        ExpectedOut = [(loopOutSwap, dummyOutResp)]
        ExpectedIn = [(loopInSwap, dummyInResp)]
    }

    do! ctx.RunStep(step)
  }
