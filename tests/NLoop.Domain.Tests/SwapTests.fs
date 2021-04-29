module SwapTests

open System
open System.Threading.Tasks
open NLoop.Domain.IO
open Xunit
open FsCheck
open FsCheck.Xunit
open Generators
open NBitcoin
open NLoop.Domain
open FSharp.Control.Tasks

type SwapDomainTests() =
  let mockBroadcaster =
    { new IBroadcaster
        with
        member this.BroadcastTx(tx, cryptoCode) = Task.CompletedTask }
  let mockFeeEstimator =
    { new IFeeEstimator
        with
        member this.Estimate(cryptoCode) = FeeRate(10m) |> Task.FromResult }

  let mockDeps = {
    Swap.Deps.Broadcaster = mockBroadcaster
    Swap.Deps.FeeEstimator = mockFeeEstimator }

  let getAggr() =
    let s = Swap.State.Zero
    { Swap.Aggregate.Zero = s
      Apply = Swap.applyChanges
      Exec = Swap.executeCommand }

  do
    Arb.register<PrimitiveGenerator>() |> ignore

  [<Property(MaxTest=10)>]
  member this.AddSwapWithSameIdShouldReturnError (loopOut) =
    let aggr = getAggr()
    let state = aggr.Zero
    let t = aggr.Exec mockDeps (state) (Swap.Command.NewLoopOut(loopOut))
    let r =
      t.GetAwaiter().GetResult() |> Result.deref |> Seq.exactlyOne
    Assert.Equal(Swap.Event.NewLoopOutAdded(loopOut), r)
    let state = aggr.Apply(state) (r)
    let out = Assert.Single(state.OnGoing.Out)
    Assert.Equal(out, loopOut)

    let t = aggr.Exec mockDeps (state) (Swap.Command.NewLoopOut(loopOut))
    let r =
      t.GetAwaiter().GetResult() |> Result.deref |> Seq.exactlyOne
    Assert.Equal(Swap.Event.KnownSwapAddedAgain(loopOut.Id), r)
    let state = aggr.Apply(state) (r)
    let out = Assert.Single(state.OnGoing.Out)
    Assert.Equal(out, loopOut)
    ()


  [<Property(MaxTest=10)>]
  member this.``SwapUpdate (BogusDepsWillRaiseException)`` (loopOut) =
    // prepare
    let aggr = getAggr()
    let bogusFeeEstimator = {
      new IFeeEstimator with
        member this.Estimate(cryptoCode) = task {
          return raise <| Exception("Failed!")
        }
    }
    let state = aggr.Zero
    let state =
      let t = aggr.Exec mockDeps (state) (Swap.Command.NewLoopOut({ loopOut with Status = SwapStatusType.Created }))
      let evt = t.GetAwaiter().GetResult() |> Result.deref |> Seq.exactlyOne
      aggr.Apply(state) (evt)

    // (Check exception)
    let swapUpdate =
      let tx = Network.RegTest.CreateTransaction()
      { Swap.Data.SwapStatusUpdate.Id = loopOut.Id
        Swap.Data.SwapStatusUpdate.Response = {
          Swap.Data.SwapStatusResponseData._Status = "transaction.confirmed"
          Transaction = Some({ Tx = tx
                               TxId = tx.GetWitHash()
                               Eta = 1 })
          FailureReason = None }
        Swap.Data.SwapStatusUpdate.Network = Network.RegTest }
    let e = Assert.ThrowsAsync<Exception>(fun () -> aggr.Exec mockDeps (state) (Swap.Command.SwapUpdate(swapUpdate)) :> Task)
    Assert.Equal(e.GetAwaiter().GetResult().Message, "Failed!")
    ()
