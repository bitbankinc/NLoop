module SwapTests

open RandomUtils
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
open FsToolkit.ErrorHandling

type SwapDomainTests() =
  let useRealDB = false
  let assureRunSynchronously = useRealDB
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

  do
    Arb.register<PrimitiveGenerator>() |> ignore
    Arb.register<DomainTypeGenerator>() |> ignore

  let getCommand (effectiveDate: DateTime) msg =
    { ESCommand.Data = msg
      Meta = {
        CommandMeta.Source = "Test"
        EffectiveDate = effectiveDate |> UnixDateTime.Create |> Result.deref
      }
    }

  let getTestRepository() =
    let store = InMemoryStore.eventStore()
    Repository.Create
      store
      Swap.serializer
      "swap in-memory repo"

  let executeCommand deps repo swapId useRealDB =
    fun cmd -> taskResult {
      let aggr = Swap.getAggregate deps
      let handler =
        if useRealDB then
          Swap.getHandler aggr ("tcp://admin:changeit@localhost:1113" |> Uri)
        else
          Handler.Create<_> (aggr) (repo)
      let! events = handler.Execute swapId cmd
      do! Async.Sleep 10
      return events
    }

  let commandsToEvents(assureRunSequentially) deps repo swapId useRealDB commands =
    if (assureRunSequentially) then
      commands
      |> List.map(fun cmd -> (executeCommand deps repo swapId useRealDB cmd) |> fun t -> t.GetAwaiter().GetResult())
      |> List.sequenceResultM
      |> Result.map(List.concat)
    else
      commands
      |> List.map(executeCommand deps repo swapId useRealDB)
      |> List.sequenceTaskResultM
      |> TaskResult.map(List.concat)
      |> fun t -> t.GetAwaiter().GetResult()

  [<Fact>]
  member this.JsonSerializerTest() =
    let events = [
      Swap.Event.LoopErrored("Error msg")
      Swap.Event.ClaimTxPublished(uint256.Zero)
      Swap.Event.SwapTxPublished(uint256.One)
      Swap.Event.ReceivedOffChainPayment(PaymentPreimage.Create(Array.zeroCreate(32)))
    ]

    for e in events do
      let ser = Swap.serializer
      let e2 = ser.EventToBytes(e) |> ser.BytesToEvents
      Assertion.isOk(e2)

  [<Property(MaxTest=10)>]
  member this.JsonSerializerTest_LoopIn(loopIn: LoopIn) =
    let e = Swap.Event.NewLoopInAdded(loopIn)
    let ser = Swap.serializer
    let e2 = ser.EventToBytes(e) |> ser.BytesToEvents
    Assertion.isOk(e2)

  [<Property(MaxTest=10)>]
  member this.JsonSerializerTest_LoopOut(loopOut: LoopOut) =
    let e = Swap.Event.NewLoopOutAdded(loopOut)
    let ser = Swap.serializer
    let e2 = ser.EventToBytes(e) |> ser.BytesToEvents
    Assertion.isOk(e2)

  [<Property(MaxTest=10)>]
  member this.EventMetaSerialize(em: EventMeta) =
    let emr = em.ToBytes() |> EventMeta.FromBytes
    Assertion.isOk(emr)
    let em2 = emr |> Result.deref
    Assert.Equal(em, em2)

  [<Property(MaxTest=10)>]
  member this.SerializedEventSerialize(se: SerializedEvent) =
    let ser = se.ToBytes() |> SerializedEvent.FromBytes
    Assertion.isOk(ser)
    let se2 = ser |> Result.deref
    Assert.Equal(se, se2)

  [<Property(MaxTest=10)>]
  member this.TestNewLoopOut(loopOut: LoopOut) =
    let loopOut = { loopOut with Id = SwapId(Guid.NewGuid().ToString()) }
    let commands =
      [
        (DateTime(2001, 01, 30, 0, 0, 0), Swap.Msg.NewLoopOut(loopOut))
      ]
      |> List.map(fun x -> x ||> getCommand)
    let events =
      let deps = mockDeps(None)
      let repo = getTestRepository()
      commandsToEvents (assureRunSynchronously) deps repo loopOut.Id useRealDB commands
    Assertion.isOk events

  [<Property(MaxTest=10)>]
  member this.TestLoopOut(loopOut: LoopOut) =
    let loopOut = { loopOut with Id = SwapId(Guid.NewGuid().ToString()) }
    let commands =
      [
        let loopOut =
          let claimKey = new Key()
          let claimAddr =
            claimKey.PubKey.WitHash.GetAddress(Network.RegTest)
          let paymentPreimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
          let paymentHash = paymentPreimage.Hash
          let refundKey = new Key()
          let redeemScript =
            Scripts.reverseSwapScriptV1(paymentHash) claimKey.PubKey refundKey.PubKey (loopOut.TimeoutBlockHeight)
          { loopOut with
              Preimage = paymentPreimage
              ClaimKey = claimKey
              OnChainAmount = Money.Max(loopOut.OnChainAmount, Money.Satoshis(100000m))
              RedeemScript = redeemScript
              ClaimAddress = claimAddr.ToString(); }
        (DateTime(2001, 01, 30, 0, 0, 0), Swap.Msg.NewLoopOut(loopOut))
        let update =
          let swapTx =
            let fee = Money.Satoshis(30m)
            let txb = Network.RegTest.CreateTransactionBuilder()
            txb
              .AddRandomFunds(loopOut.OnChainAmount + fee + Money.Coins(1m))
              .Send(loopOut.RedeemScript.WitHash.ScriptPubKey, loopOut.OnChainAmount)
              .SendFees(fee)
              .SetChange((new Key()).PubKey.WitHash)
              .BuildTransaction(true)
          { Swap.Data.SwapStatusUpdate.Network = Network.RegTest
            Swap.Data.Response = {
              Swap.Data.SwapStatusResponseData._Status = "transaction.confirmed"
              Transaction = Some({ Tx = swapTx
                                   TxId = swapTx.GetWitHash()
                                   Eta = 1 })
              FailureReason = None
            }
          }
        (DateTime(2001, 01, 30, 1, 0, 0), Swap.Msg.SwapUpdate(update))
      ]
      |> List.map(fun x -> x ||> getCommand)
    let events =
      let fundsKey = new Key()
      let deps = { mockDeps(None) with UTXOProvider = mockUtxoProvider([|fundsKey|]) }
      let repo = getTestRepository()
      commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB commands
    Assertion.isOk events

  [<Property(MaxTest=10)>]
  member this.TestLoopIn(loopIn: LoopIn) =
    let loopIn = { loopIn with Id = SwapId(Guid.NewGuid().ToString()) }
    let commands =
      [
        let addr =
          use key = new Key()
          key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest)
        let loopIn = {
          loopIn with
            LoopIn.Address = addr.ToString()
            ExpectedAmount = if loopIn.ExpectedAmount.Satoshi < 0L then Money.Coins(0.5m) else loopIn.ExpectedAmount
          }
        (DateTime(2001, 01, 30, 0, 0, 0), Swap.Msg.NewLoopIn(loopIn))

        let swapUpdate =
          { Swap.Data.SwapStatusUpdate.Response = {
              Swap.Data.SwapStatusResponseData._Status = "invoice.set"
              Transaction = None
              FailureReason = None }
            Swap.Data.SwapStatusUpdate.Network = Network.RegTest }
        (DateTime(2001, 01, 30, 1, 0, 0), Swap.Msg.SwapUpdate(swapUpdate))
      ]
      |> List.map(fun x -> x ||> getCommand)
    let events =
      use fundsKey = new Key()
      let deps = { mockDeps(None) with UTXOProvider = mockUtxoProvider([|fundsKey|]) }
      let repo = getTestRepository()
      commandsToEvents assureRunSynchronously deps repo loopIn.Id useRealDB commands
    Assertion.isOk events

