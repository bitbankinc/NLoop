module SwapTests

open DotNetLightning.Payment
open NBitcoin.Altcoins
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
  let getChangeAddress = GetAddress(fun _cryptoCode ->
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
      Swap.Deps.GetRefundAddress = getChangeAddress }

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
      Swap.Event.LoopErrored(SwapId("foo"), "Error msg")
      Swap.Event.ClaimTxPublished(uint256.Zero)
      Swap.Event.SwapTxPublished(Network.RegTest.CreateTransaction().ToHex())
    ]

    for e in events do
      let ser = Swap.serializer
      let e2 = ser.EventToBytes(e) |> ser.BytesToEvents
      Assertion.isOk(e2)

  [<Property(MaxTest=10)>]
  member this.JsonSerializerTest_LoopIn(loopIn: LoopIn, height: uint32) =
    let e = Swap.Event.NewLoopInAdded(height |> BlockHeight, loopIn)
    let ser = Swap.serializer
    let e2 = ser.EventToBytes(e) |> ser.BytesToEvents
    Assertion.isOk(e2)

  [<Property(MaxTest=10)>]
  member this.JsonSerializerTest_LoopOut(loopOut: LoopOut, height: uint32) =
    let e = Swap.Event.NewLoopOutAdded(height |> BlockHeight,loopOut)
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
  member this.TestNewLoopOut(loopOut: LoopOut, height: uint32) =
    let loopOut = {
      loopOut with
        Id = SwapId(Guid.NewGuid().ToString())
        OnChainAmount = Money.Max(loopOut.OnChainAmount, Money.Satoshis(10000m))
    }

    let commands =
      [
        (DateTime(2001, 01, 30, 0, 0, 0), Swap.Command.NewLoopOut(height |> BlockHeight, loopOut))
      ]
      |> List.map(fun x -> x ||> getCommand)
    let events =
      let deps = mockDeps(None)
      let repo = getTestRepository()
      commandsToEvents (assureRunSynchronously) deps repo loopOut.Id useRealDB commands
    Assertion.isOk events

  [<Property(MaxTest=10)>]
  member this.TestLoopOut(loopOut: LoopOut, testAltcoin: bool) =
    let ourCryptoCode =
      if testAltcoin then SupportedCryptoCode.LTC else SupportedCryptoCode.BTC
    let loopOut =
      { loopOut with
          Id = SwapId(Guid.NewGuid().ToString())
          ChainName = Network.RegTest.ChainName.ToString()
          PairId = (ourCryptoCode, SupportedCryptoCode.BTC)
        }
    let commands =
      [
        let paymentPreimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
        let loopOut =
          let claimKey = new Key()
          let claimAddr =
            claimKey.PubKey.WitHash.GetAddress(loopOut.OurNetwork)
          let paymentHash = paymentPreimage.Hash
          let refundKey = new Key()
          let redeemScript =
            Scripts.reverseSwapScriptV1(paymentHash) claimKey.PubKey refundKey.PubKey (loopOut.TimeoutBlockHeight)
          let invoice =
            let fields = { TaggedFields.Fields = [ PaymentHashTaggedField paymentHash; DescriptionTaggedField "test" ] }
            PaymentRequest.TryCreate(loopOut.TheirNetwork, None, DateTimeOffset.UtcNow, fields, new Key())
            |> ResultUtils.Result.deref
          { loopOut with
              Preimage = paymentPreimage
              Invoice = invoice.ToString()
              ClaimKey = claimKey
              OnChainAmount = Money.Max(loopOut.OnChainAmount, Money.Satoshis(100000m))
              RedeemScript = redeemScript
              ClaimAddress = claimAddr.ToString(); }
        (DateTime(2001, 01, 30, 0, 0, 0), Swap.Command.NewLoopOut(BlockHeight.One, loopOut))
        let update =
          let swapTx =
            let fee = Money.Satoshis(30m)
            let txb =
              loopOut
                .OurNetwork
                .CreateTransactionBuilder()
            txb
              .AddRandomFunds(loopOut.OnChainAmount + fee + Money.Coins(1m))
              .Send(loopOut.RedeemScript.WitHash.ScriptPubKey, loopOut.OnChainAmount)
              .SendFees(fee)
              .SetChange((new Key()).PubKey.WitHash)
              .BuildTransaction(true)
          {
            Swap.Data.SwapStatusResponseData._Status = "transaction.confirmed"
            Swap.Data.SwapStatusResponseData.Transaction =
              Some({ Tx = swapTx
                     TxId = swapTx.GetWitHash()
                     Eta = 1 })
            Swap.Data.SwapStatusResponseData.FailureReason = None
          }

        (DateTime(2001, 01, 30, 1, 0, 0), Swap.Command.SwapUpdate(update))
        (DateTime(2001, 01, 30, 2, 0, 0), Swap.Command.OffChainOfferResolve(paymentPreimage))
      ]
      |> List.map(fun x -> x ||> getCommand)
    let events =
      let fundsKey = new Key()
      let deps = { mockDeps(None) with UTXOProvider = mockUtxoProvider([|fundsKey|]) }
      let repo = getTestRepository()
      commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB commands
    Assertion.isOk events
    let lastEvent =
      events
      |> Result.deref
      |> List.last
    Assert.Equal(Swap.Event.SuccessfullyFinished(loopOut.Id).Type, lastEvent.Data.Type)

  [<Property(MaxTest=10)>]
  member this.TestLoopIn_Timeout(loopIn: LoopIn, testAltcoin: bool) =
    let theirCryptoCode =
      if testAltcoin then SupportedCryptoCode.LTC else SupportedCryptoCode.BTC
    let ourCryptoCode = SupportedCryptoCode.BTC
    let loopIn =
      { loopIn with
          Id = SwapId(Guid.NewGuid().ToString())
          ChainName = Network.RegTest.ChainName.ToString()
          PairId = (ourCryptoCode, theirCryptoCode)
        }
    let commands =
      [
        let addr =
          use key = new Key()
          key.PubKey.GetAddress(ScriptPubKeyType.Segwit, loopIn.TheirNetwork)
        let preimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
        let initialBlockHeight = BlockHeight.One
        let loopIn = {
          loopIn with
            LoopIn.Address = addr.ToString()
            ExpectedAmount = if loopIn.ExpectedAmount.Satoshi <= 1000L then Money.Coins(0.5m) else loopIn.ExpectedAmount
            TimeoutBlockHeight = initialBlockHeight + BlockHeightOffset16(3us)
            RedeemScript =
              let remoteClaimKey = new Key()
              let timeoutBlockHeight = initialBlockHeight + BlockHeightOffset16(3us)
              Scripts.swapScriptV1
                preimage.Hash
                (remoteClaimKey.PubKey)
                (loopIn.RefundPrivateKey.PubKey)
                timeoutBlockHeight
          }

        (DateTime(2001, 01, 30, 0, 0, 0), Swap.Command.NewLoopIn(initialBlockHeight, loopIn))

        let swapUpdate =
          {
            Swap.Data.SwapStatusResponseData._Status = "invoice.set"
            Swap.Data.SwapStatusResponseData.Transaction = None
            Swap.Data.SwapStatusResponseData.FailureReason = None
          }
        (DateTime(2001, 01, 30, 1, 0, 0), Swap.Command.SwapUpdate(swapUpdate))

        let nextHeight = initialBlockHeight + BlockHeightOffset16(1us)
        (DateTime(2001, 01, 30, 2, 0, 0), Swap.Command.NewBlock(nextHeight, theirCryptoCode))
        let nextHeight = initialBlockHeight + BlockHeightOffset16(2us)
        (DateTime(2001, 01, 30, 3, 0, 0), Swap.Command.NewBlock(nextHeight, theirCryptoCode))
        let nextHeight = initialBlockHeight + BlockHeightOffset16(3us)
        (DateTime(2001, 01, 30, 4, 0, 0), Swap.Command.NewBlock(nextHeight, theirCryptoCode))
      ]
      |> List.map(fun x -> x ||> getCommand)

    let mutable txBroadcasted = 0
    let events =
      use fundsKey = new Key()
      let deps =
        let mockBroadcaster = {
          new IBroadcaster with
            member this.BroadcastTx(tx, cc) =
              txBroadcasted <- txBroadcasted + 1
              Task.CompletedTask
        }
        { mockDeps(None) with
            UTXOProvider = mockUtxoProvider([|fundsKey|])
            Broadcaster = mockBroadcaster }
      let repo = getTestRepository()
      commandsToEvents assureRunSynchronously deps repo loopIn.Id useRealDB commands
    Assertion.isOk events
    Assert.Equal(2, txBroadcasted) // swap tx and refund tx

    let lastEvent =
      events
      |> Result.deref
      |> List.last
    Assert.Equal(Swap.Event.FinishedByRefund(loopIn.Id).Type, lastEvent.Data.Type)

  [<Property(MaxTest=10)>]
  member this.TestLoopIn(loopIn: LoopIn, testAltcoin: bool) =
    let theirCryptoCode =
      if testAltcoin then SupportedCryptoCode.LTC else SupportedCryptoCode.BTC
    let loopIn =
      { loopIn with
          Id = SwapId(Guid.NewGuid().ToString())
          ChainName = Network.RegTest.ChainName.ToString()
          PairId = (SupportedCryptoCode.BTC, theirCryptoCode) }
    let commands =
      [
        let addr =
          use key = new Key()
          key.PubKey.GetAddress(ScriptPubKeyType.Segwit, loopIn.TheirNetwork)
        let loopIn = {
          loopIn with
            LoopIn.Address = addr.ToString()
            ExpectedAmount = if loopIn.ExpectedAmount.Satoshi <= 1000L then Money.Coins(0.5m) else loopIn.ExpectedAmount
          }
        let initialBlockHeight = BlockHeight.One
        (DateTime(2001, 01, 30, 0, 0, 0), Swap.Command.NewLoopIn(initialBlockHeight, loopIn))

        let swapUpdate =
          {
            Swap.Data.SwapStatusResponseData._Status = "invoice.set"
            Swap.Data.SwapStatusResponseData.Transaction = None
            Swap.Data.SwapStatusResponseData.FailureReason = None
          }
        (DateTime(2001, 01, 30, 1, 0, 0), Swap.Command.SwapUpdate(swapUpdate))
        (DateTime(2001, 01, 30, 2, 0, 0), Swap.Command.OffChainPaymentReception)
      ]
      |> List.map(fun x -> x ||> getCommand)
    let events =
      use fundsKey = new Key()
      let deps = { mockDeps(None) with UTXOProvider = mockUtxoProvider([|fundsKey|]) }
      let repo = getTestRepository()
      commandsToEvents assureRunSynchronously deps repo loopIn.Id useRealDB commands
    Assertion.isOk events
    let lastEvent =
      events
      |> Result.deref
      |> List.last
    Assert.Equal(Swap.Event.SuccessfullyFinished(loopIn.Id).Type, lastEvent.Data.Type)
    ()
