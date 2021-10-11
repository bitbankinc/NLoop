module SwapTests

open DotNetLightning.Payment
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
        member this.Estimate target cryptoCode = FeeRate(10m) |> Task.FromResult }

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

  let mockDeps() =
    {
      Swap.Deps.Broadcaster = mockBroadcaster
      Swap.Deps.FeeEstimator = mockFeeEstimator
      Swap.Deps.UTXOProvider = mockUtxoProvider [||]
      Swap.Deps.GetChangeAddress = getChangeAddress
      Swap.Deps.GetRefundAddress = getChangeAddress
      Swap.Deps.PayInvoice =
        fun _n _parameters _req ->
          Task.CompletedTask
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
          Handler.Create<_> aggr repo
      let! events = handler.Execute swapId cmd
      do! Async.Sleep 10
      return events
    }

  let commandsToEvents assureRunSequentially deps repo swapId useRealDB commands =
    if assureRunSequentially then
      commands
      |> List.map(executeCommand deps repo swapId useRealDB >> fun t -> t.GetAwaiter().GetResult())
      |> List.sequenceResultM
      |> Result.map(List.concat)
    else
      commands
      |> List.map(executeCommand deps repo swapId useRealDB)
      |> List.sequenceTaskResultM
      |> TaskResult.map(List.concat)
      |> fun t -> t.GetAwaiter().GetResult()

  let getDummyTestInvoice(network: Network) =
    assert(network <> null)
    let paymentPreimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
    let paymentHash = paymentPreimage.Hash
    let fields = { TaggedFields.Fields = [ PaymentHashTaggedField paymentHash; DescriptionTaggedField "test" ] }
    PaymentRequest.TryCreate(network, None, DateTimeOffset.UtcNow, fields, new Key())
    |> ResultUtils.Result.deref
    |> fun x -> x.ToString()

  [<Fact>]
  member this.JsonSerializerTest() =
    let events = [
      Swap.Event.FinishedByError(SwapId("foo"), "Error msg")
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
  member this.TestNewLoopOut(loopOut: LoopOut, loopOutParams: Swap.LoopOutParams) =
    let loopOut = {
      loopOut with
        Id = SwapId(Guid.NewGuid().ToString())
        OnChainAmount = Money.Max(loopOut.OnChainAmount, Money.Satoshis(10000m))
        ChainName = ChainName.Regtest.ToString()
        PairId = (SupportedCryptoCode.LTC, SupportedCryptoCode.BTC)
    }
    let loopOut = {
      loopOut with
        Invoice = getDummyTestInvoice(loopOut.QuoteAssetNetwork)
        PrepayInvoice = getDummyTestInvoice(loopOut.QuoteAssetNetwork)
    }
    let commands =
      [
        (DateTime(2001, 01, 30, 0, 0, 0), Swap.Command.NewLoopOut(loopOutParams, loopOut))
      ]
      |> List.map(fun x -> x ||> getCommand)
    let events =
      let deps = mockDeps()
      let repo = getTestRepository()
      commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB commands
    Assertion.isOk events

  [<Property(MaxTest=10)>]
  member this.TestLoopOut_Success(loopOut: LoopOut, loopOutParams: Swap.LoopOutParams, testAltcoin: bool) =
    let baseAsset =
       if testAltcoin then SupportedCryptoCode.LTC else SupportedCryptoCode.BTC
    let loopOut =
      { loopOut with
          Id = SwapId(Guid.NewGuid().ToString())
          ChainName = Network.RegTest.ChainName.ToString()
          PairId = (baseAsset, SupportedCryptoCode.BTC)
        }
    let paymentPreimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
    let timeoutBlockHeight = BlockHeight(3u)
    let loopOut =
      let claimKey = new Key()
      let claimAddr =
        claimKey.PubKey.WitHash.GetAddress(loopOut.BaseAssetNetwork)
      let paymentHash = paymentPreimage.Hash
      let refundKey = new Key()
      let redeemScript =
        Scripts.reverseSwapScriptV1(paymentHash) claimKey.PubKey refundKey.PubKey loopOut.TimeoutBlockHeight
      let invoice =
        let fields = { TaggedFields.Fields = [ PaymentHashTaggedField paymentHash; DescriptionTaggedField "test" ] }
        PaymentRequest.TryCreate(loopOut.QuoteAssetNetwork, None, DateTimeOffset.UtcNow, fields, new Key())
        |> ResultUtils.Result.deref
      { loopOut
          with
          Preimage = paymentPreimage
          TimeoutBlockHeight = timeoutBlockHeight
          Invoice = invoice.ToString()
          PrepayInvoice = getDummyTestInvoice(loopOut.QuoteAssetNetwork)
          ClaimKey = claimKey
          OnChainAmount = Money.Max(loopOut.OnChainAmount, Money.Satoshis(100000m))
          RedeemScript = redeemScript
          ClaimTransactionId = None
          LockupTransactionHex = None
          AcceptZeroConf = true
          Status = SwapStatusType.SwapCreated
          MaxMinerFee = Money.Coins(10m)
          ClaimAddress = claimAddr.ToString(); }
    let commands =
      [
        let loopOutParams = {
          loopOutParams with
            Swap.LoopOutParams.Height = BlockHeight.One
        }
        (DateTime(2001, 01, 30, 0, 0, 0), Swap.Command.NewLoopOut(loopOutParams, loopOut))
        let update =
          let swapTx =
            let fee = Money.Satoshis(30m)
            let txb =
              loopOut
                .BaseAssetNetwork
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
                     Eta = Some 1 })
            Swap.Data.SwapStatusResponseData.FailureReason = None
          }

        (DateTime(2001, 01, 30, 1, 0, 0), Swap.Command.SwapUpdate(update))

      ]
      |> List.map(fun x -> x ||> getCommand)

    let txBroadcasted = ResizeArray()
    let deps =
      let broadcaster = {
        new IBroadcaster with
          member this.BroadcastTx(tx, cc) =
            txBroadcasted.Add(tx)
            Task.CompletedTask
      }
      { mockDeps() with
          UTXOProvider =
            let fundsKey = new Key()
            mockUtxoProvider([|fundsKey|])
          Broadcaster = broadcaster }
    let repo = getTestRepository()
    let _ =
      let events =
        commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB commands
      Assertion.isOk events
      let lastEvent =
        events
        |> Result.deref
        |> List.last
      Assert.Equal(Swap.Event.ClaimTxPublished(null).Type, lastEvent.Data.Type)

    let claimTx = Assert.Single(txBroadcasted)
    let confirmationCommands =
      [
        let emptyBlock =
          match baseAsset with
          | SupportedCryptoCode.LTC ->
            Altcoins.Litecoin.LitecoinBlock.CreateBlock(Network.RegTest)
          | SupportedCryptoCode.BTC ->
            Block.CreateBlock(loopOut.BaseAssetNetwork)
          | _ -> failwith "unreachable"
        (DateTime(2001, 01, 30, 2, 0, 0), Swap.Command.NewBlock(BlockHeight(2u), emptyBlock, baseAsset))
        let block =
          match baseAsset with
          | SupportedCryptoCode.LTC ->
            Altcoins.Litecoin.LitecoinBlock.CreateBlock(loopOut.BaseAssetNetwork)
          | SupportedCryptoCode.BTC ->
            Block.CreateBlock(loopOut.BaseAssetNetwork)
          | _ -> failwith "unreachable"
        block.AddTransaction(claimTx) |> ignore
        let nextHeight = BlockHeight(3u)
        assert(nextHeight = timeoutBlockHeight)
        (DateTime(2001, 01, 30, 3, 0, 0), Swap.Command.NewBlock(nextHeight, block, baseAsset))
      ]
      |> List.map(fun x -> x ||> getCommand)
    let events =
      commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB confirmationCommands
    let lastEvent =
      events
      |> Result.deref
      |> List.last
    Assert.Equal(Swap.Event.FinishedSuccessfully(loopOut.Id), lastEvent.Data)

  /// It should time out when the server does not tell us about swap tx that they ought to published
  /// after we made an offer.
  [<Property(MaxTest=10)>]
  member this.TestLoopOut_Timeout(loopOut: LoopOut, loopOutParams: Swap.LoopOutParams) =
    let currentHeight = loopOutParams.Height
    let loopOut = {
      loopOut with
        Id = SwapId(Guid.NewGuid().ToString())
        OnChainAmount = Money.Max(loopOut.OnChainAmount, Money.Satoshis(10000m))
        ChainName = ChainName.Regtest.ToString()
        PairId = (SupportedCryptoCode.LTC, SupportedCryptoCode.BTC)
        ClaimTransactionId = None
        LockupTransactionHex = None
        TimeoutBlockHeight = currentHeight + BlockHeightOffset32(30u)
    }
    let loopOut = {
      loopOut with
        Invoice = getDummyTestInvoice(loopOut.QuoteAssetNetwork)
        PrepayInvoice = getDummyTestInvoice(loopOut.QuoteAssetNetwork)
    }
    let commands =
      [
        (DateTime(2001, 01, 30, 0, 0, 0), Swap.Command.NewLoopOut(loopOutParams, loopOut))
        let mutable i = 0
        yield!
          [ for h in currentHeight.Value..loopOut.TimeoutBlockHeight.Value ->
              i <- i + 1
              (DateTime(2001, 01, 30, 0, 3 + i, 0), Swap.Command.NewBlock(BlockHeight(h), Altcoins.Litecoin.LitecoinBlock.CreateBlock(Network.RegTest), SupportedCryptoCode.LTC))
            ]
      ]
      |> List.map(fun x -> x ||> getCommand)
    let events =
      let deps = mockDeps()
      let repo = getTestRepository()
      commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB commands
    Assertion.isOk events
    let lastEvent = events |> Result.deref |> List.last
    Assert.Equal(Swap.Event.FinishedByTimeout("").Type, lastEvent.Data.Type)

  [<Property(MaxTest=10)>]
  member this.TestLoopIn_Timeout(loopIn: LoopIn, testAltcoin: bool) =
    let quoteAsset =
      if testAltcoin then SupportedCryptoCode.LTC else SupportedCryptoCode.BTC
    let baseAsset = SupportedCryptoCode.BTC
    let loopIn =
      { loopIn with
          Id = SwapId(Guid.NewGuid().ToString())
          ChainName = Network.RegTest.ChainName.ToString()
          PairId = (baseAsset, quoteAsset)
        }
    let commands =
      [
        let addr =
          use key = new Key()
          key.PubKey.GetAddress(ScriptPubKeyType.Segwit, loopIn.QuoteAssetNetwork)
        let preimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
        let initialBlockHeight = BlockHeight.One
        let timeoutBlockHeight = initialBlockHeight + BlockHeightOffset16(3us)
        let loopIn = {
          loopIn with
            LoopIn.Address = addr.ToString()
            ExpectedAmount = if loopIn.ExpectedAmount.Satoshi <= 1000L then Money.Coins(0.5m) else loopIn.ExpectedAmount
            TimeoutBlockHeight = timeoutBlockHeight
            RedeemScript =
              let remoteClaimKey = new Key()
              Scripts.swapScriptV1
                preimage.Hash
                remoteClaimKey.PubKey
                loopIn.RefundPrivateKey.PubKey
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
        let block = Altcoins.Litecoin.LitecoinBlock.CreateBlock(Network.RegTest)
        (DateTime(2001, 01, 30, 2, 0, 0), Swap.Command.NewBlock(nextHeight, block,quoteAsset))
        let nextHeight = initialBlockHeight + BlockHeightOffset16(2us)
        (DateTime(2001, 01, 30, 3, 0, 0), Swap.Command.NewBlock(nextHeight, block, quoteAsset))
        let nextHeight = initialBlockHeight + BlockHeightOffset16(3us)
        assert(nextHeight = timeoutBlockHeight)
        (DateTime(2001, 01, 30, 4, 0, 0), Swap.Command.NewBlock(nextHeight, block, quoteAsset))
      ]
      |> List.map(fun x -> x ||> getCommand)

    let mutable txBroadcasted = 0
    let lockObj = obj()
    let events =
      use fundsKey = new Key()
      let deps =
        let mockBroadcaster = {
          new IBroadcaster with
            member this.BroadcastTx(tx, cc) =
              lock lockObj <| fun () ->
                txBroadcasted <- txBroadcasted + 1
              Task.CompletedTask
        }
        { mockDeps() with
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
    Assert.Equal(Swap.Event.FinishedByRefund(loopIn.Id), lastEvent.Data)

  [<Property(MaxTest=40)>]
  member this.TestLoopIn_Success(loopIn: LoopIn, testAltcoin: bool) =
    let quoteAsset =
      if testAltcoin then SupportedCryptoCode.LTC else SupportedCryptoCode.BTC
    let loopIn =
      { loopIn with
          Id = SwapId(Guid.NewGuid().ToString())
          ChainName = Network.RegTest.ChainName.ToString()
          PairId = (SupportedCryptoCode.BTC, quoteAsset) }
    let commands =
      [
        let addr =
          use key = new Key()
          key.PubKey.GetAddress(ScriptPubKeyType.Segwit, loopIn.QuoteAssetNetwork)
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
        let swapUpdate = {
          swapUpdate with
            _Status = "transaction.claimed"
        }
        (DateTime(2001, 01, 30, 2, 0, 0), Swap.Command.SwapUpdate(swapUpdate))
      ]
      |> List.map(fun x -> x ||> getCommand)
    let events =
      use fundsKey = new Key()
      let deps = { mockDeps() with UTXOProvider = mockUtxoProvider([|fundsKey|]) }
      let repo = getTestRepository()
      commandsToEvents assureRunSynchronously deps repo loopIn.Id useRealDB commands
    Assertion.isOk events
    let lastEvent =
      events
      |> Result.deref
      |> List.last
    Assert.Equal(Swap.Event.FinishedSuccessfully(loopIn.Id), lastEvent.Data)
    ()
