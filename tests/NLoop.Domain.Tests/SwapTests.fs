module SwapTests

open DotNetLightning.Payment
open DotNetLightning.Utils
open NLoop.Domain
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

[<AutoOpen>]
module Helpers =
  let getDummyTestInvoice(network: Network) =
    assert(network <> null)
    let paymentPreimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
    let paymentHash = paymentPreimage.Hash
    let fields = { TaggedFields.Fields = [ PaymentHashTaggedField paymentHash; DescriptionTaggedField "test" ] }
    PaymentRequest.TryCreate(network, Some(LNMoney.Satoshis(100000L)), DateTimeOffset.UtcNow, fields, new Key())
    |> ResultUtils.Result.deref
    |> fun x -> x.ToString()

  type LoopOut with
    member this.Normalize() =
      { this with
          Id = SwapId(Guid.NewGuid().ToString())
          ChainName = Network.RegTest.ChainName.ToString()
          IsOffchainOfferResolved = false
          IsClaimTxConfirmed = false
          ClaimTransactionId = None
          LockupTransactionHex = None
          Status = SwapStatusType.SwapCreated
          MaxMinerFee = Money.Coins(10m)
          OnChainAmount = Money.Max(this.OnChainAmount, Money.Satoshis(10000m))
          LockupTransactionHeight = None
        }
    member loopOut.Sanitize(paymentPreimage: PaymentPreimage, timeoutBlockHeight, onChainAmount, acceptZeroConf) =
      let claimKey = new Key()
      let claimAddr =
        claimKey.PubKey.WitHash.GetAddress(loopOut.BaseAssetNetwork)
      let paymentHash = paymentPreimage.Hash
      let refundKey = new Key()
      let redeemScript =
        Scripts.reverseSwapScriptV1(paymentHash) claimKey.PubKey refundKey.PubKey loopOut.TimeoutBlockHeight
      let invoice =
        let fields = { TaggedFields.Fields = [ PaymentHashTaggedField paymentHash; DescriptionTaggedField "test" ] }
        PaymentRequest.TryCreate(loopOut.QuoteAssetNetwork, Some(LNMoney.Satoshis(100000L)), DateTimeOffset.UtcNow, fields, new Key())
        |> ResultUtils.Result.deref
      { loopOut
          with
          Preimage = paymentPreimage
          TimeoutBlockHeight = timeoutBlockHeight
          Invoice = invoice.ToString()
          PrepayInvoice = getDummyTestInvoice(loopOut.QuoteAssetNetwork)
          ClaimKey = claimKey
          OnChainAmount = onChainAmount
          RedeemScript = redeemScript
          SwapTxConfRequirement =
            if acceptZeroConf then
              BlockHeightOffset32.Zero
            else
              BlockHeightOffset32(3u)
          ClaimAddress = claimAddr.ToString(); }
  type LoopIn with
    member this.Normalize() =
      { this with
          Id = SwapId(Guid.NewGuid().ToString())
          ChainName = Network.RegTest.ChainName.ToString()
          LockupTransactionOutPoint = None
          RefundTransactionId = None
          Status = SwapStatusType.SwapCreated
          MaxMinerFee = Money.Coins(10m)
        }

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
        fun _n _parameters req ->
          let r = {
            Swap.PayInvoiceResult.AmountPayed = req.AmountValue |> Option.defaultValue(LNMoney.Satoshis(100000L))
            Swap.PayInvoiceResult.RoutingFee = LNMoney.Satoshis(10L)
          }
          Task.FromResult(r)
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

  let getBlock (loopIn: LoopIn) =
    let block =
      let struct(_, quoteAsset) = loopIn.PairId
      match quoteAsset with
      | SupportedCryptoCode.LTC ->
        Altcoins.Litecoin.LitecoinBlock.CreateBlock(loopIn.QuoteAssetNetwork)
      | SupportedCryptoCode.BTC ->
        Block.CreateBlock(loopIn.QuoteAssetNetwork)
      | _ -> failwith "unreachable"
    block
  let getBlockOut (loopOut: LoopOut) =
    let struct (baseAsset, _) = loopOut.PairId
    match baseAsset with
    | SupportedCryptoCode.LTC ->
      Altcoins.Litecoin.LitecoinBlock.CreateBlock(loopOut.BaseAssetNetwork)
    | SupportedCryptoCode.BTC ->
      Block.CreateBlock(loopOut.BaseAssetNetwork)
    | _ -> failwith "unreachable"


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

  let assertNotUnknownEvent (e: ESEvent<_>) =
    e.Data |> function | Swap.Event.UnknownTagEvent(t, _) -> failwith $"unknown tag {t}" | _ -> e
  let getLastEvent e =
      e
      |> Result.deref
      |> List.map(assertNotUnknownEvent)
      |> List.last

  [<Fact>]
  member this.JsonSerializerTest() =
    let events = [
      Swap.Event.FinishedByError(SwapId("foo"), "Error msg")
      Swap.Event.ClaimTxPublished(uint256.Zero)
      Swap.Event.TheirSwapTxPublished(Network.RegTest.CreateTransaction().ToHex())
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
        LockupTransactionHeight = None
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
  member this.TestLoopOut_Success(loopOut: LoopOut, loopOutParams: Swap.LoopOutParams, testAltcoin: bool, acceptZeroConf: bool) =
    let baseAsset =
       if testAltcoin then SupportedCryptoCode.LTC else SupportedCryptoCode.BTC
    let loopOut = { loopOut.Normalize() with PairId = (baseAsset, SupportedCryptoCode.BTC) }

    let paymentPreimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
    let timeoutBlockHeight = BlockHeight(30u)
    let onChainAmount = Money.Max(loopOut.OnChainAmount, Money.Satoshis(100000m))
    let loopOut =
      loopOut.Sanitize(paymentPreimage, timeoutBlockHeight, onChainAmount, acceptZeroConf)
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
    let commands =
      [
        let loopOutParams = {
          loopOutParams with
            Swap.LoopOutParams.Height = BlockHeight.One
        }
        (DateTime(2001, 01, 30, 0, 0, 0), Swap.Command.NewLoopOut(loopOutParams, loopOut))
        (DateTime(2001, 01, 30, 1, 0, 0), Swap.Command.CommitSwapTxInfoFromCounterParty(swapTx.ToHex()))
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
      Assert.Contains(Swap.Event.TheirSwapTxPublished(swapTx.ToHex()), events |> Result.deref |> List.map(fun e -> e.Data))

      let lastEvent =
        events |> getLastEvent
      let expected =
        if acceptZeroConf then
          Swap.Event.ClaimTxPublished(null).Type
        else
          Swap.Event.TheirSwapTxPublished(null).Type
      Assert.Equal(expected, lastEvent.Data.Type)

    let _ =
      // first confirmation
      let commands =
        [
          let block = getBlockOut(loopOut)
          block.AddTransaction(swapTx) |> ignore
          (DateTime(2001, 01, 30, 2, 0, 0), Swap.Command.NewBlock(BlockHeight(2u), block, baseAsset))
        ]
        |> List.map(fun x -> x ||> getCommand)
      let events =
        commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB commands
      Assert.Contains(Swap.Event.TheirSwapTxConfirmedFirstTime, events |> Result.deref |> List.map(fun e -> e.Data))
      if acceptZeroConf then
        let lastEvent = events |> getLastEvent
        let expected = Swap.Event.ClaimTxPublished(null).Type
        Assert.Equal(expected, lastEvent.Data.Type)

    let _ =
      // confirm until our claim tx gets published for sure.
      let commands =
        [
          for i in 1..3 ->
            let h = BlockHeight((i + 2) |> uint)
            (DateTime(2001, 01, 30, 3 + i, 0, 0), Swap.Command.NewBlock(h, getBlockOut(loopOut), baseAsset))
        ]
        |> List.map(fun x -> x ||> getCommand)
      let events =
        commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB commands
        |> Result.deref
      if not <| acceptZeroConf then
        let expected =
          Swap.Event.ClaimTxPublished(null).Type
        Assert.Contains(expected, events |> List.map(fun e -> e.Data.Type))

    let claimTx =
      txBroadcasted |> Seq.last
    let confirmationCommands =
      [
        let block = getBlockOut(loopOut)
        block.AddTransaction(claimTx) |> ignore
        let nextHeight = BlockHeight(6u)
        (DateTime(2001, 01, 30, 7, 0, 0), Swap.Command.NewBlock(nextHeight, block, baseAsset))
      ]
      |> List.map(fun x -> x ||> getCommand)
    let lastEvent =
      commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB confirmationCommands
      |> getLastEvent
    let serverFee = LNMoney.Satoshis(1000L)
    let sweepAmount = loopOut.OnChainAmount - serverFee.ToMoney()
    let expected =
      let sweepTxId = txBroadcasted |> Seq.last |> fun t -> t.GetHash()
      Swap.Event.SweepTxConfirmed(sweepTxId, sweepAmount)
    Assert.Equal(expected.Type, lastEvent.Data.Type)
    Assert.True(Money.Zero < sweepAmount && sweepAmount < loopOut.OnChainAmount)
    let offChainSolvedCommands =
      [
        let r =
          let routingFee = LNMoney.Satoshis(100L)
          { Swap.PayInvoiceResult.AmountPayed = onChainAmount.ToLNMoney() + serverFee
            Swap.PayInvoiceResult.RoutingFee = routingFee }
        (DateTime(2001, 01, 30, 8, 0, 0), Swap.Command.OffChainOfferResolve(r))
      ]
      |> List.map(fun x -> x ||> getCommand)
    let lastEvent =
      commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB offChainSolvedCommands
      |> getLastEvent
    Assert.Equal(Swap.Event.FinishedSuccessfully(loopOut.Id), lastEvent.Data)

  /// It should time out when the server does not tell us about swap tx that they ought to published
  /// after we made an offer.
  [<Property(MaxTest=10)>]
  member this.TestLoopOut_Timeout(loopOutBase: LoopOut, loopOutParams: Swap.LoopOutParams, acceptZeroConf) =
    let RunAndAssertFinishedByTimeout (loopOut: LoopOut) commands =
      let loopOut = { loopOut with Id = (Guid.NewGuid()).ToString() |> SwapId }
      let commands =
        (DateTime(2001, 01, 30, 0, 0, 0), Swap.Command.NewLoopOut(loopOutParams, loopOut)) :: commands
        |> List.map(fun x -> x ||> getCommand)
      let lastEvent =
        let deps = mockDeps()
        let repo = getTestRepository()
        commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB commands
        |> getLastEvent
      Assert.Equal(Swap.Event.FinishedByTimeout(loopOut.Id, "").Type, lastEvent.Data.Type)

    let currentHeight = loopOutParams.Height
    let confirmCommandsUntilTimeout (loopOut: LoopOut) =
      let mutable i = 0
      [ for h in currentHeight.Value..loopOut.TimeoutBlockHeight.Value ->
          i <- i + 1
          let struct(baseAsset, _) = loopOut.PairId
          (DateTime(2001, 01, 30, 0, 3 + i, 0), Swap.Command.NewBlock(BlockHeight(h), getBlockOut(loopOut), baseAsset))
        ]
    let loopOut = { loopOutBase.Normalize() with PairId = (SupportedCryptoCode.BTC, SupportedCryptoCode.BTC) }

    // case 1: nothing happens after creation.
    let _ =
      let loopOut = {
        loopOut with
          Invoice = getDummyTestInvoice(loopOut.QuoteAssetNetwork)
          PrepayInvoice = getDummyTestInvoice(loopOut.QuoteAssetNetwork)
          TimeoutBlockHeight = BlockHeight(30u)
      }
      confirmCommandsUntilTimeout loopOut |> RunAndAssertFinishedByTimeout loopOut
    // case 2: nothing happens after they tell us about the swap tx.
    let _ =
      let loopOut =
        let paymentPreimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
        let timeoutBlockHeight = BlockHeight(30u)
        let onChainAmount = Money.Max(loopOut.OnChainAmount, Money.Satoshis(100000m))
        loopOut.Sanitize(paymentPreimage, timeoutBlockHeight, onChainAmount, acceptZeroConf)
      let commands =
        [
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
          (DateTime(2001, 01, 30, 1, 0, 0), Swap.Command.CommitSwapTxInfoFromCounterParty(swapTx.ToHex()))
        ]
      (commands @ confirmCommandsUntilTimeout loopOut)|> RunAndAssertFinishedByTimeout loopOut
    ()

  [<Property(MaxTest=10)>]
  member this.TestLoopOut_Reorg(loopOut: LoopOut, loopOutParams: Swap.LoopOutParams, testAltcoin, acceptZeroConf: bool) =
    let baseAsset =
       if testAltcoin then SupportedCryptoCode.LTC else SupportedCryptoCode.BTC
    let loopOut =
      let quoteAsset = SupportedCryptoCode.BTC
      { loopOut.Normalize() with PairId = (baseAsset, quoteAsset) }
    let initialBlockHeight = BlockHeight.One
    let timeoutBlockHeight = initialBlockHeight + BlockHeightOffset16(30us)
    let paymentPreimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
    let onChainAmount = Money.Max(loopOut.OnChainAmount, Money.Satoshis(100000m))
    let loopOut = {
      loopOut.Sanitize(paymentPreimage, timeoutBlockHeight, onChainAmount, acceptZeroConf)
        with
        TimeoutBlockHeight = timeoutBlockHeight
    }
    let one = initialBlockHeight + BlockHeightOffset32.One
    let two = initialBlockHeight + BlockHeightOffset32(2u)
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
    let confirmationCommands =
      [
        let loopOutParams = { loopOutParams with Height = initialBlockHeight }
        (DateTime(2001, 01, 30, 1, 0, 0), Swap.Command.NewLoopOut(loopOutParams, loopOut))
        (DateTime(2001, 01, 30, 2, 0, 0), Swap.Command.CommitSwapTxInfoFromCounterParty(swapTx.ToHex()))
        let block = getBlockOut(loopOut)
        block.AddTransaction(swapTx) |> ignore
        (DateTime(2001, 01, 30, 3, 0, 0), Swap.Command.NewBlock(one, block, baseAsset))
      ]

    let deps = mockDeps()
    let repo = getTestRepository()
    let emptyBlock = getBlockOut(loopOut)
    // 1-block reorg
    let _ =
      let loopOut = { loopOut with Id = (Guid.NewGuid()).ToString() |> SwapId }
      let commands =
        confirmationCommands @ [
          // empty block with the same block height with the one swap tx confirmed.
          (DateTime(2001, 01, 30, 5, 0, 0), Swap.Command.NewBlock(one, emptyBlock, baseAsset))
        ]
        |> List.map(fun x -> x ||> getCommand)
      let lastEvent =
        commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB commands
        |> getLastEvent
      // must emit an reorg event
      Assert.Equal(Swap.Event.TheirSwapTxReorgedOut, lastEvent.Data)

      let additionalCommands =
        [
        // new confirmation of the swap tx...
          let block = getBlockOut(loopOut)
          block.AddTransaction(swapTx) |> ignore
          (DateTime(2001, 01, 30, 6, 0, 0), Swap.Command.NewBlock(two, block, baseAsset))
        ]
        |> List.map(fun x -> x ||> getCommand)
      let events =
        commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB additionalCommands
      // must be detected.
      Assert.Contains(Swap.Event.TheirSwapTxConfirmedFirstTime, events |> Result.deref |> List.map(fun e -> e.Data))

    // 2-block reorg
    let _ =
      let loopOut = { loopOut with Id = (Guid.NewGuid()).ToString() |> SwapId }
      let events =
        confirmationCommands @ [
          // additional confirmation
          (DateTime(2001, 01, 30, 5, 0, 0), Swap.Command.NewBlock(two, emptyBlock, baseAsset))
          // reorg
          (DateTime(2001, 01, 30, 6, 0, 0), Swap.Command.NewBlock(initialBlockHeight, emptyBlock, baseAsset))
          (DateTime(2001, 01, 30, 7, 0, 0), Swap.Command.NewBlock(one, emptyBlock, baseAsset))
        ]
        |> List.map(fun c -> c ||> getCommand)
        |> commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB
      // must emit an reorg event
      Assert.Contains(Swap.Event.TheirSwapTxReorgedOut, events |> Result.deref |> List.map(fun e -> e.Data))

    // the new branch holds confirmation in the older block.
    let _ =
      let events =
        confirmationCommands @ [
          // additional confirmation
          (DateTime(2001, 01, 30, 5, 0, 0), Swap.Command.NewBlock(two, emptyBlock, baseAsset))
          // reorg
          let block = getBlockOut(loopOut)
          block.AddTransaction(swapTx) |> ignore
          (DateTime(2001, 01, 30, 6, 0, 0), Swap.Command.NewBlock(initialBlockHeight, block, baseAsset))
          (DateTime(2001, 01, 30, 7, 0, 0), Swap.Command.NewBlock(one, emptyBlock, baseAsset))
        ]
        |> List.map(fun c -> c ||> getCommand)
        |> commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB
      Assert.Contains(Swap.Event.TheirSwapTxReorgedOut, events |> Result.deref |> List.map(fun e -> e.Data))

      let howManyFirstConfirmations =
        events
        |> Result.deref
        |> List.filter(fun e -> match e.Data with | Swap.Event.TheirSwapTxConfirmedFirstTime -> true | _ -> false)
        |> List.length
      Assert.Equal(2, howManyFirstConfirmations)
    ()

  [<Property(MaxTest=10)>]
  member this.TestLoopIn_Timeout(loopIn: LoopIn, testAltcoin: bool) =
    /// prepare
    let quoteAsset =
      if testAltcoin then SupportedCryptoCode.LTC else SupportedCryptoCode.BTC
    let baseAsset = SupportedCryptoCode.BTC
    let loopIn =
      { loopIn.Normalize() with
          PairId = (baseAsset, quoteAsset)
        }
    let initialBlockHeight = BlockHeight.One
    let timeoutBlockHeight = initialBlockHeight + BlockHeightOffset16(3us)
    use key = new Key()
    let repo = getTestRepository()
    let txBroadcasted = ResizeArray()
    use fundsKey = new Key()
    let deps =
      let mockBroadcaster = {
        new IBroadcaster with
          member this.BroadcastTx(tx, cc) =
            txBroadcasted.Add(tx)
            Task.CompletedTask
      }
      { mockDeps() with
          UTXOProvider = mockUtxoProvider([|fundsKey|])
          Broadcaster = mockBroadcaster }

    // act
    let events =
      let commands =
        [
          let loopIn =
            let addr =
              key.PubKey.GetAddress(ScriptPubKeyType.Segwit, loopIn.QuoteAssetNetwork)
            let preimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
            {
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

        ]
        |> List.map(fun x -> x ||> getCommand)
      commandsToEvents assureRunSynchronously deps repo loopIn.Id useRealDB commands

    // assert
    Assertion.isOk events
    let swapTx = Assert.Single(txBroadcasted)
    let lastEvent = events |> getLastEvent
    Assert.Equal(Swap.Event.OurSwapTxPublished(Money.Zero, "").Type, lastEvent.Data.Type)

    // act
    let e2 =
      let commands =
        [
          let nextHeight = initialBlockHeight + BlockHeightOffset16(1us)
          let block =
            let b = getBlock(loopIn)
            b.AddTransaction(swapTx) |> ignore
            b
          (DateTime(2001, 01, 30, 2, 0, 0), Swap.Command.NewBlock(nextHeight, block,quoteAsset))
          let block = getBlock(loopIn)
          let nextHeight = initialBlockHeight + BlockHeightOffset16(2us)
          (DateTime(2001, 01, 30, 3, 0, 0), Swap.Command.NewBlock(nextHeight, block, quoteAsset))
          let nextHeight = initialBlockHeight + BlockHeightOffset16(3us)
          assert(nextHeight = timeoutBlockHeight)
          (DateTime(2001, 01, 30, 4, 0, 0), Swap.Command.NewBlock(nextHeight, block, quoteAsset))
        ]
        |> List.map(fun x -> x ||> getCommand)
      commandsToEvents assureRunSynchronously deps repo loopIn.Id useRealDB commands
    // assert
    Assert.Contains(e2 |> Result.deref, fun e -> e.Data.Type = Swap.Event.OurSwapTxConfirmed(uint256.Zero, 0u).Type)
    let lastEvent = e2 |> getLastEvent
    Assert.Equal(Swap.Event.RefundTxPublished(uint256.Zero).Type, lastEvent.Data.Type)

    // act
    let lastEvent =
      let refundConfirmCommands =
        [
          let block =
            let refundTx =
              let refundTxId = lastEvent.Data |> function | Swap.Event.RefundTxPublished refundTxId -> refundTxId | _ -> failwith "unreachable"
              txBroadcasted |> Seq.find(fun tx -> tx.GetHash() = refundTxId)
            let b = getBlock(loopIn)
            b.AddTransaction(refundTx) |> ignore
            b
          let nextHeight = initialBlockHeight + BlockHeightOffset16(4us)
          (DateTime(2001, 01, 30, 5, 0, 0), Swap.Command.NewBlock(nextHeight, block, quoteAsset))
        ]
        |> List.map(fun x -> x ||> getCommand)
      let deps = mockDeps()
      commandsToEvents assureRunSynchronously deps repo loopIn.Id useRealDB refundConfirmCommands
      |> getLastEvent
    // assert
    Assert.Equal(Swap.Event.FinishedByRefund(loopIn.Id).Type, lastEvent.Data.Type)

  [<Property(MaxTest=10)>]
  member this.TestLoopIn_Success(loopIn: LoopIn, testAltcoin: bool) =
    /// prepare
    let quoteAsset =
      if testAltcoin then SupportedCryptoCode.LTC else SupportedCryptoCode.BTC
    let baseAsset = SupportedCryptoCode.BTC
    let loopIn =
      { loopIn.Normalize() with
          PairId = (baseAsset, quoteAsset)
        }
    let initialBlockHeight = BlockHeight.One
    let timeoutBlockHeight = initialBlockHeight + BlockHeightOffset16(3us)
    use key = new Key()
    let repo = getTestRepository()
    let txBroadcasted = ResizeArray()
    use fundsKey = new Key()
    let deps =
      let mockBroadcaster = {
        new IBroadcaster with
          member this.BroadcastTx(tx, cc) =
            txBroadcasted.Add(tx)
            Task.CompletedTask
      }
      { mockDeps() with
          UTXOProvider = mockUtxoProvider([|fundsKey|])
          Broadcaster = mockBroadcaster }

    let loopIn =
      let addr =
        key.PubKey.GetAddress(ScriptPubKeyType.Segwit, loopIn.QuoteAssetNetwork)
      let preimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
      {
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
    // act
    let events =
      let commands =
        [
          (DateTime(2001, 01, 30, 0, 0, 0), Swap.Command.NewLoopIn(initialBlockHeight, loopIn))
        ]
        |> List.map(fun x -> x ||> getCommand)
      commandsToEvents assureRunSynchronously deps repo loopIn.Id useRealDB commands

    // assert
    Assertion.isOk events
    let swapTx = Assert.Single(txBroadcasted) // swap tx and refund tx
    let lastEvent = events |> getLastEvent
    Assert.Equal(Swap.Event.OurSwapTxPublished(Money.Zero, "").Type, lastEvent.Data.Type)

    // act
    let lastEvent =
      let commands =
        [
          let nextHeight = initialBlockHeight + BlockHeightOffset16(1us)
          let block =
            let b = getBlock(loopIn)
            b.AddTransaction(swapTx) |> ignore
            b
          (DateTime(2001, 01, 30, 2, 0, 0), Swap.Command.NewBlock(nextHeight, block,quoteAsset))
          let nextHeight = initialBlockHeight + BlockHeightOffset16(2us)
          let block =
            let b = getBlock(loopIn)
            let dummySuccessTx =
              let txb = loopIn.QuoteAssetNetwork.CreateTransactionBuilder()
              txb
                .AddCoins(swapTx)
                .AddKnownRedeems(loopIn.RedeemScript)
                .SendAll(new Key())
                .BuildTransaction(false)
            b.AddTransaction(dummySuccessTx) |> ignore
            b
          (DateTime(2001, 01, 30, 3, 0, 0), Swap.Command.NewBlock(nextHeight, block, quoteAsset))
        ]
        |> List.map(fun x -> x ||> getCommand)
      commandsToEvents assureRunSynchronously deps repo loopIn.Id useRealDB commands
      |> getLastEvent

    // assert
    Assert.Equal(Swap.Event.FinishedSuccessfully(loopIn.Id).Type, lastEvent.Data.Type)

