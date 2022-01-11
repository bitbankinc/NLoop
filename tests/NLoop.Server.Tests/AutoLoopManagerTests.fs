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

(*
type AutoLoopManagerTests() =
  let onChain = SupportedCryptoCode.BTC
  member private this.TestAutoLoopManager(step: AutoLoopStep,
                                          group: Swap.Group,
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
                Assert.Equal(Swap.Category.Out, group.Category)
                { SwapDTO.OutTermsResponse.MaxSwapAmount = step.MaxAmount
                  MinSwapAmount = step.MinAmount }
              LoopInTerms = fun _ ->
                Assert.Equal(Swap.Category.In, group.Category)
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
    let man = (getManager SupportedCryptoCode.BTC).Value
    let! r = man.SetParameters parameters
    Assertion.isOk r

    do! man.RunStep(CancellationToken.None)
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

    let group = {
      Swap.Group.Category = Swap.Category.Out
      Swap.Group.PairId = pairId
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
    do! this.TestAutoLoopManager(step, group, parameters, channels)
    let step = AutoLoopStep.Create(min=chan1Rec.Amount.Satoshi + 1L, max=chan1Rec.Amount.Satoshi)
    do! this.TestAutoLoopManager(step, group, parameters, channels)
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
    let step = {
      AutoLoopStep.MinAmount = 1L |> Money.Satoshis
      MaxAmount = failwith "todo"
      OngoingOut = failwith "todo"
      OngoingIn = failwith "todo"
      FailedOut = failwith "todo"
      FailedIn = failwith "todo"
      QuotesOut = failwith "todo"
      QuotesIn = failwith "todo"
      ExpectedOut = failwith "todo"
      ExpectedIn = failwith "todo" }
    let group = {
      Swap.Group.Category = Swap.Category.Out
      Swap.Group.PairId = pairId
    }
    //do! this.TestAutoLoopManager(step, group, parameters, channels)
    ()
  }
*)
