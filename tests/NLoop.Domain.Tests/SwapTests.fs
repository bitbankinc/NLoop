module SwapTests

open System
open System.Threading.Tasks
open DotNetLightning.Utils.Primitives
open NLoop.Domain.Utils
open Xunit
open FsCheck
open FsCheck.Xunit
open Generators
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open FSharp.Control.Tasks
open FsToolkit.ErrorHandling

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

  let mockLightningClient paymentPreimage = {
    new INLoopLightningClient
      with
      member this.Offer(cc, o) =
        paymentPreimage
        |> Task.FromResult
  }

  let mockDeps maybePaymentPreimage =
    let pp = maybePaymentPreimage |> Option.defaultValue (PaymentPreimage.Create(Array.zeroCreate(32)))
    {
    Swap.Deps.Broadcaster = mockBroadcaster
    Swap.Deps.FeeEstimator = mockFeeEstimator
    Swap.Deps.UTXOProvider = mockUtxoProvider ([||])
    Swap.Deps.GetChangeAddress = getChangeAddress
    Swap.Deps.LightningClient = mockLightningClient pp
  }

  let getAggr(maybePaymentPreimage) =
    let deps = mockDeps (maybePaymentPreimage)
    Swap.getAggregate deps

  do
    Arb.register<PrimitiveGenerator>() |> ignore

  let getCommand effectiveDate msg =
    { ESCommand.Data = msg; Meta = { CommandMeta.Source = "Test"; EffectiveDate = effectiveDate } }

  let executeCommand deps swapId =
    fun cmd -> taskResult {
      let aggr = Swap.getAggregate deps
      let handler = Swap.getHandler aggr ("tcp://admin:changeit@localhost:1113" |> Uri)
      let! events = handler.Execute swapId cmd
      do! Async.Sleep 300
      return events
    }

  [<Property(MaxTest=10)>]
  member this.TestLoopOut(loopOut: LoopOut) =
    let commands =
      [
        (DateTime(2001, 01, 30, 0, 0, 0), Swap.Msg.NewLoopOut(loopOut))
      ]
      |> List.map(fun x -> x ||> getCommand)
    let deps = mockDeps(None)
    let events =
      commands
      |> List.map(executeCommand deps loopOut.Id)
      |> List.sequenceTaskResultM
      |> TaskResult.map(List.concat)
      |> fun t -> t.GetAwaiter().GetResult()
    Assert.isOk events
    ()


  (*
  [<Property(MaxTest=10)>]
  member this.AddSwapWithSameIdShouldReturnError (loopOut) =
    let aggr = getAggr(None)
    let state = aggr.Zero
    let t = aggr.Exec (state) ((Swap.Msg.NewLoopOut(loopOut)) |> getCommand (DateTime(2001, 01, 30, 0, 0, 0)))
    let r =
      t.GetAwaiter().GetResult() |> Result.deref |> fst |> Seq.exactlyOne
    Assert.Equal(Swap.Event.NewLoopOutAdded(loopOut), r.Data)
    let state = aggr.Apply(state) (r.Data)
    let _ =
      let s = state |> function | Swap.State.Out x -> x | x -> failwithf "%A" x
      Assert.Equal(s, loopOut)

    let t = aggr.Exec (state) (Swap.Msg.NewLoopOut(loopOut) |> getCommand(DateTime(2001, 01, 30, 1, 0, 0)))

    Assert.ThrowsAny(Action(fun () ->  t.GetAwaiter().GetResult() |> Result.deref |> ignore))


  [<Property(MaxTest=10)>]
  member this.``SwapUpdate (BogusDepsWillRaiseException)`` (loopOut) =
    // prepare
    let aggr = getAggr(None)
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

    let aggr =
      let mockDeps = { mockDeps(None) with FeeEstimator = bogusFeeEstimator }
      Swap.getAggregate mockDeps
    let state =
      let cmd = Swap.Msg.NewLoopOut({ loopOut with Status = SwapStatusType.SwapCreated }) |> getCommand(DateTime(2001, 01, 30, 0, 0, 0))
      let t = aggr.Exec (state) cmd
      let evt = t.GetAwaiter().GetResult() |> Result.deref |> fst |> Seq.exactlyOne
      aggr.Apply(state) (evt.Data)

    // (Check exception)
    let swapUpdate =
      let tx = Network.RegTest.CreateTransaction()
      { Swap.Data.SwapStatusUpdate.Response = {
        Swap.Data.SwapStatusResponseData._Status = "transaction.confirmed"
        Transaction = Some({ Tx = tx
                             TxId = tx.GetWitHash()
                             Eta = 1 })
        FailureReason = None }
        Swap.Data.SwapStatusUpdate.Network = Network.RegTest }
    let e = Assert.ThrowsAsync<Exception>(fun () -> aggr.Exec (state) (Swap.Msg.SwapUpdate(swapUpdate) |> getCommand(DateTime(2001, 01, 30, 0, 0, 0))) :> Task)
    Assert.Equal(e.GetAwaiter().GetResult().Message, "Failed!")
    ()

  [<Property(MaxTest=10)>]
  member this.``SwapUpdate(LoopIn)``(loopIn: LoopIn, prevTxo: OutPoint) =
    // setup
    let aggr = getAggr(None)
    let state = aggr.Zero
    let addr =
      use key = new Key()
      key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest)
    let loopIn = {
      loopIn with
        LoopIn.Address = addr.ToString()
        ExpectedAmount = if loopIn.ExpectedAmount.Satoshi < 0L then Money.Coins(0.5m) else loopIn.ExpectedAmount
    }

    // act and assert
    let aggr =
      let key = new Key()
      { mockDeps(None) with
          UTXOProvider = mockUtxoProvider [|key|]
      }
      |> Swap.getAggregate
    let t =
      aggr.Exec (state) (Swap.Msg.NewLoopIn(loopIn) |> getCommand(DateTime(2001, 01, 30, 0, 0, 0)))
    let r =
      t.GetAwaiter().GetResult() |> Result.deref |> fst |> Seq.exactlyOne
    Assert.Equal(Swap.Event.NewLoopInAdded(loopIn), r.Data)
    let state = aggr.Apply(state) (r.Data)
    Assert.Equal(state, Swap.State.In loopIn)

    let swapUpdate =
      { Swap.Data.SwapStatusUpdate.Response = {
        Swap.Data.SwapStatusResponseData._Status = "invoice.set"
        Transaction = None
        FailureReason = None }
        Swap.Data.SwapStatusUpdate.Network = Network.RegTest }
    let t = aggr.Exec (state) (Swap.Msg.SwapUpdate(swapUpdate) |> getCommand(DateTime(2001, 01, 30, 1, 0, 0)))
    let r =
      t.GetAwaiter().GetResult() |> Result.deref |> fst |> Seq.exactlyOne
    ()
    *)

