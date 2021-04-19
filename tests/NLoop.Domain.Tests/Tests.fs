module Tests

open System.Threading.Tasks
open Xunit
open FsCheck
open FsCheck.Xunit
open Generators
open NBitcoin
open NLoop.Domain

type SwapDomainTests() =
  let mockBroadcaster =
    { new IBroadcaster
        with
        member this.BroadcastTx(tx, cryptoCode) = Task.CompletedTask }
  let mockFeeEstimator =
    { new IFeeEstimator
        with
        member this.Estimate(cryptoCode) = FeeRate(10m) |> Task.FromResult }

  let getAggr() =
    let s = Swap.State.Create(mockBroadcaster, mockFeeEstimator)
    { Swap.Aggregate.Zero = s
      Apply = Swap.applyChanges
      Exec = Swap.executeCommand }

  do
    Arb.register<PrimitiveGenerator>() |> ignore

  [<Property(MaxTest=50)>]
  member this.``AddSwapWithSameIdShouldReturnError`` (loopOut) =
    let aggr = getAggr()
    let state = aggr.Zero
    let t = aggr.Exec(state) (Swap.Command.NewLoopOut(loopOut))
    let r =
      t.GetAwaiter().GetResult() |> Result.deref |> Seq.exactlyOne
    Assert.Equal(Swap.Event.NewLoopOutAdded(loopOut), r)
    let state = aggr.Apply(state) (r)
    let out = Assert.Single(state.OnGoing.Out)
    Assert.Equal(out, loopOut)

    let t = aggr.Exec(state) (Swap.Command.NewLoopOut(loopOut))
    let r =
      t.GetAwaiter().GetResult() |> Result.deref |> Seq.exactlyOne
    Assert.Equal(Swap.Event.KnownSwapAddedAgain(loopOut.Id), r)
    let state = aggr.Apply(state) (r)
    let out = Assert.Single(state.OnGoing.Out)
    Assert.Equal(out, loopOut)
    ()

