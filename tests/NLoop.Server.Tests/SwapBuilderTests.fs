namespace NLoop.Server.Tests

open System
open System.Threading.Tasks
open DotNetLightning.Utils
open FSharp.Control.Tasks
open DotNetLightning.Utils.Primitives
open LndClient
open Microsoft.Extensions.Logging.Abstractions
open NBitcoin
open NLoop.Domain
open NLoop.Server
open NLoop.Server.DTOs
open NLoop.Server.RPCDTOs
open NLoop.Server.Services
open NLoop.Server.SwapServerClient
open Xunit

[<AutoOpen>]
module private SwapBuilderTestHelpers =
  let peer1 = NodeId <| PubKey("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619")
  let chan1 = ShortChannelId.FromUInt64(1UL)
  let peer2 = NodeId <| PubKey("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c")
  let chan2 = ShortChannelId.FromUInt64(2UL)

  let swapAmount = Money.Satoshis 100000L

  let htlcConfTarget = BlockHeightOffset32 6u
  let chan1Info =
    {
      GetChannelInfoResponse.Capacity = Money.Satoshis(10000m)
      Node1Policy = {
        Id = (new Key()).PubKey
        TimeLockDelta = BlockHeightOffset16 10us
        MinHTLC = LNMoney.Satoshis 10
        FeeBase = LNMoney.Satoshis 10
        FeeProportionalMillionths = 2u
        Disabled = false
      }
      Node2Policy = {
        Id = (new Key()).PubKey
        TimeLockDelta = BlockHeightOffset16 10us
        MinHTLC = LNMoney.Satoshis 10
        FeeBase = LNMoney.Satoshis 10
        FeeProportionalMillionths = 2u
        Disabled = false
      }
    }

type SwapBuilderTests() =

  let testConfig =
    {
      LoopInSwapBuilderDeps.GetLoopInQuote = fun req -> task {
        return! TestHelpers.GetDummySwapServerClient().GetLoopInQuote(req)
      }
    }

  static member TestLoopInUseData =
    seq [
      ("swap allowed", [peer2], [chan2], [peer2], Ok())
      ("conflicts with loop out", [], [chan1], [], Error(SwapDisqualifiedReason.LoopOutAlreadyInTheChannel))
      ("conflicts with loop in", [peer1], [], [], Error(SwapDisqualifiedReason.LoopInAlreadyInTheChannel))
      ("previously failed loop in", [], [], [peer1], Error(SwapDisqualifiedReason.FailureBackoff))
    ]
    |> Seq.map(fun (name, ongoingLoopIn, ongoingLoopOut, failedLoopIn, expected) -> [|
      name |> box
      ongoingLoopIn |> box
      ongoingLoopOut |> box
      failedLoopIn |> box
      expected |> box
    |])

  [<Theory>]
  [<MemberData(nameof(SwapBuilderTests.TestLoopInUseData))>]
  member this.TestLoopInUse(_name: string,
                            ongoingLoopIn: NodeId list,
                            ongoingLoopOut: ShortChannelId list,
                            failedLoopIn: NodeId list,
                            expected: Result<unit, SwapDisqualifiedReason>) =
    let traffic = {
      SwapTraffic.OngoingLoopIn = ongoingLoopIn
      OngoingLoopOut = ongoingLoopOut
      FailedLoopOut = Map.empty
      FailedLoopIn = failedLoopIn |> List.map(fun l -> (l, testTime)) |> Map.ofSeq
    }
    let builder = SwapBuilder.NewLoopIn(testConfig, NullLogger.Instance)
    let err = builder.VerifyTargetIsNotInUse traffic { Peer = peer1; Channels = [|chan1|] }
    Assertion.isSame(expected, err)


  static member TestLoopInBuildSwapData =
    let quoteRequest = {
      SwapDTO.LoopInQuoteRequest.Amount = swapAmount
      SwapDTO.LoopInQuoteRequest.Pair = pairId
      SwapDTO.LoopInQuoteRequest.HtlcConfTarget = htlcConfTarget
    }
    let quote = {
      SwapDTO.LoopInQuote.SwapFee = Money.Satoshis(1L)
      SwapDTO.LoopInQuote.MinerFee = Money.Satoshis(2L)
    }
    let expectedSwap =
      SwapSuggestion.In({
        LoopInRequest.Amount = swapAmount
        ChannelId = chanId1 |> Some
        Label = None
        PairId = pairId |> Some
        MaxMinerFee = quote.MinerFee |> ValueSome
        MaxSwapFee = quote.SwapFee |> ValueSome
        HtlcConfTarget = htlcConfTarget.Value |> int |> ValueSome
        LastHop = peer1.Value |> Some })
    seq [
      ("quote successful", quoteRequest, Ok quote, Ok(expectedSwap))
      ("client unreachable", quoteRequest, Error "foo", Error(SwapDisqualifiedReason.LoopInUnReachable "foo"))
    ]
    |> Seq.map(fun (name,
                    expectedQuoteRequest,
                    quote: Result<SwapDTO.LoopInQuote, string>,
                    expected: Result<SwapSuggestion, SwapDisqualifiedReason>) -> [|
      name |> box
      expectedQuoteRequest |> box
      quote |> box
      expected |> box
    |])
  [<Theory>]
  [<MemberData(nameof(SwapBuilderTests.TestLoopInBuildSwapData))>]
  member this.TestLoopInBuildSwap(_name: string,
                                  expectedQuoteRequest: SwapDTO.LoopInQuoteRequest,
                                  quote: Result<SwapDTO.LoopInQuote, string>,
                                  expected: Result<SwapSuggestion, SwapDisqualifiedReason>) = task {
    let config = {
      testConfig
        with
        GetLoopInQuote = fun req ->
          Assert.Equal(expectedQuoteRequest, req)
          quote |> Task.FromResult
    }
    let builder = SwapBuilder.NewLoopIn(config, NullLogger.Instance)
    let parameters = {
      Parameters.Default SupportedCryptoCode.BTC
        with
        HTLCConfTarget = htlcConfTarget
    }

    let! err =
      builder.BuildSwap
        { TargetPeerOrChannel.Peer = peer1; Channels = [|chan1|] }
        swapAmount
        pairId
        false
        parameters
    Assertion.isSame(expected, err)
    ()
  }
