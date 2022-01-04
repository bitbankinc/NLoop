namespace NLoop.Server.Tests

open DotNetLightning.Utils.Primitives
open Microsoft.Extensions.Logging.Abstractions
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
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

type SwapBuilderTests() =

  let testConfig =
    {
      EstimateFee = TestHelpers.GetDummyFeeEstimator()
      SwapServerClient = TestHelpers.GetDummySwapServerClient()
      Restrictions = fun (cat: Swap.Category) -> failwith "todo"
      Lnd = TestHelpers.GetDummyLightningClient()
      SwapActor = TestHelpers.GetDummySwapActor()
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
  member this.TestLoopInUse(name: string,
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

