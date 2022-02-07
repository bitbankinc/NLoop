namespace NLoop.Server.Tests

open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Domain
open NLoop.Server
open NLoop.Server.Services
open NLoop.Server.SwapServerClient
open Xunit

[<AutoOpen>]
module ThresholdRuleTests =
  let testCalculateAmountData: obj[] seq =
    seq [
      let balanceBase = {
        Balances.Channels = ResizeArray()
        CapacitySat = 0L |> Money.Satoshis
        IncomingSat = 0L |> Money.Satoshis
        OutGoingSat = 0L |> Money.Satoshis
        PubKey = peer1 |> NodeId
      }
      [|
        let b = {
          balanceBase with
            CapacitySat = Money.Satoshis 100L
            IncomingSat = Money.Satoshis 20L
            OutGoingSat = Money.Satoshis 20L
        }
        box "insufficient outgoing"; b; 40s; 40s; 0L; 50s;
      |]
      [|
        let b = {
          balanceBase with
            CapacitySat = Money.Satoshis 100L
            IncomingSat = Money.Satoshis 20L
            OutGoingSat = Money.Satoshis 80L
        }
        box "loop out"; b; 20s; 60s; 50L; 50s
      |]
      [|
        let b = {
          balanceBase with
            CapacitySat = Money.Satoshis 100L
            IncomingSat = Money.Satoshis 20L
            OutGoingSat = Money.Satoshis 30L
        }
        box "pending htlcs"; b; 20s; 60s; 0L; 50s;
      |]
      [|
        let b = {
          balanceBase with
            CapacitySat = Money.Satoshis 100L
            IncomingSat = Money.Satoshis 50L
            OutGoingSat = Money.Satoshis 50L
        }
        box "loop in"; b; 60s; 30s; 0L; 50s
      |]
      [|
        let b = {
          balanceBase with
            CapacitySat = Money.Satoshis 100L
            IncomingSat = Money.Satoshis 50L
            OutGoingSat = Money.Satoshis 50L
        }
        box "liquidity ok"; b; 40s; 40s; 0L; 50s
      |]
    ]

  [<Theory>]
  [<MemberData(nameof(testCalculateAmountData))>]
  let testCalculateAmount
    (_name: string)
    (balances: Balances)
    (minOutgoing: int16<percent>)
    (minIncoming: int16<percent>)
    (amt: int64)
    (targetLiquidityRatio: int16<percent>) =

    let actualAmt =
      AutoLoopManagerExtensions.calculateSwapAmount
        balances.IncomingSat
        balances.OutGoingSat
        balances.CapacitySat
        minIncoming
        minOutgoing
        targetLiquidityRatio
    let expectedAmt = amt |> Money.Satoshis
    Assert.Equal(expectedAmt, actualAmt)


  let testSuggestSwapData: obj[] list =
    [
      [| "liquidity ok";         10s; 10s;  10L;  100L; 100L;  50L;  50L; 50s;  0L |]
      [| "loop out";             40s; 40s;  10L;  100L; 100L;   0L; 100L; 50s; 50L |]
      [| "amount below minimum"; 40s; 40s; 200L;  300L; 100L;   0L; 100L; 50s;  0L |]
      [| "amount above maximum"; 40s; 40s;  10L;   20L; 100L;   0L; 100L; 50s; 20L |]
      [| "loop in";              10s; 10s;  10L;  100L; 100L; 100L;   0L; 50s;  0L |]
    ]

  [<Theory>]
  [<MemberData(nameof(testSuggestSwapData))>]
  let testSwapAmount
    (_name: string)
    (minIncoming)
    (minOutgoing)
    (minSat: int64)
    (maxSat: int64)
    (capSat: int64)
    (inComingSat: int64)
    (outgoingSat: int64)
    (ratio)
    (expectedAmt: int64)
    =
    let rule = { ThresholdRule.MinimumIncoming = minIncoming; MinimumOutGoing = minOutgoing }
    let restriction = { ServerRestrictions.Minimum = minSat |> Money.Satoshis;  Maximum = maxSat |> Money.Satoshis; }
    let channelBalance = {
      Balances.CapacitySat = capSat |> Money.Satoshis
      IncomingSat = inComingSat |> Money.Satoshis
      OutGoingSat = outgoingSat |> Money.Satoshis
      Channels = ResizeArray()
      PubKey = peer1 |> NodeId
    }
    Assertion.isOk <| rule.Validate()
    let amt = rule.SwapAmount(channelBalance, restriction, Swap.Category.Out, ratio)
    Assert.Equal(expectedAmt, amt.Satoshi)
    ()
