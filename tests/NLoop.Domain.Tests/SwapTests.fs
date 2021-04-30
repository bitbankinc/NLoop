module SwapTests

open System
open System.Threading.Tasks
open Xunit
open FsCheck
open FsCheck.Xunit
open Generators
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
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

  let mockUtxoProvider(keys: Key []) =
    { new IUTXOProvider
        with
        member this.GetUTXOs(a, _cryptoCode) =
          let utxos =
            keys |> Seq.map(fun key ->
              let prevOutput = TxOut(Money.Coins(1m), key.PubKey.WitHash)
              let prevTxo = OutPoint(RandomUtils.GetUInt256(), RandomUtils.GetUInt32())
              Coin(prevTxo, prevOutput) :> ICoin
            )
          utxos
          |> Ok
          |> Task.FromResult
        member this.SignSwapTxPSBT(psbt, _cryptoCode) =
          psbt.SignWithKeys(keys)
          |> Task.FromResult
    }
  let getChangeAddress = GetChangeAddress(fun _cryptoCode ->
      (new Key()).PubKey.WitHash
      :> IDestination
      |> Ok
      |> Task.FromResult
    )
  let mockDeps = {
    Swap.Deps.Broadcaster = mockBroadcaster
    Swap.Deps.FeeEstimator = mockFeeEstimator
    Swap.Deps.UTXOProvider = mockUtxoProvider ([||])
    Swap.Deps.GetChangeAddress = getChangeAddress }

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
    let loopOut =
      let addr =
        use key = new Key()
        key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest)
      { loopOut with LoopOut.ClaimAddress = addr.ToString() }

    let mockDeps = { mockDeps with FeeEstimator = bogusFeeEstimator }
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

  [<Property(MaxTest=10)>]
  member this.``SwapUpdate(LoopIn)``(loopIn: LoopIn, prevTxo: OutPoint) =
    // setup
    let aggr = getAggr()
    let state = aggr.Zero
    let addr =
      use key = new Key()
      key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest)
    let loopIn = {
      loopIn with
        LoopIn.Address = addr.ToString()
        ExpectedAmount = if loopIn.ExpectedAmount.Satoshi < 0L then Money.Coins(0.5m) else loopIn.ExpectedAmount
    }
    let mockDeps =
     let key = new Key()
     { mockDeps with
         UTXOProvider = mockUtxoProvider [|key|]
     }

    // act and assert
    let t = aggr.Exec mockDeps (state) (Swap.Command.NewLoopIn(loopIn))
    let r =
      t.GetAwaiter().GetResult() |> Result.deref |> Seq.exactlyOne
    Assert.Equal(Swap.Event.NewLoopInAdded(loopIn), r)
    let state = aggr.Apply(state) (r)
    let loopInState = Assert.Single(state.OnGoing.In)
    Assert.Equal(loopInState, loopIn)

    let swapUpdate =
      { Swap.Data.SwapStatusUpdate.Id = loopIn.Id
        Swap.Data.SwapStatusUpdate.Response = {
          Swap.Data.SwapStatusResponseData._Status = "invoice.set"
          Transaction = None
          FailureReason = None }
        Swap.Data.SwapStatusUpdate.Network = Network.RegTest }
    let t = aggr.Exec mockDeps (state) (Swap.Command.SwapUpdate(swapUpdate))
    let r =
      t.GetAwaiter().GetResult() |> Result.deref |> Seq.exactlyOne
    let (_txid, swapId) = r |> function Swap.Event.SwapTxPublished(a, b) -> a, b | x -> failwithf "%A" x
    Assert.Equal(swapId, loopIn.Id)
    ()

