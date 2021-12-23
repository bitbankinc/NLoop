module SwapTests

open DotNetLightning.Payment
open DotNetLightning.Utils
open NBitcoin.DataEncoders
open NLoop.Domain
open RandomUtils
open System
open System.Threading.Tasks
open NLoop.Domain.Utils
open Xunit
open FsCheck
open FsCheck.Xunit
open Generators
open NBitcoin
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

  let hex = HexEncoder()
  let privKey1 = new Key(hex.DecodeData("0101010101010101010101010101010101010101010101010101010101010101"))
  let privKey2 = new Key(hex.DecodeData("0202020202020202020202020202020202020202020202020202020202020202"))
  let privKey3 = new Key(hex.DecodeData("0303030303030303030303030303030303030303030303030303030303030303"))
  let privKey4 = new Key(hex.DecodeData("0404040404040404040404040404040404040404040404040404040404040404"))
  let privKey5 = new Key(hex.DecodeData("0505050505050505050505050505050505050505050505050505050505050505"))
  let privKey6 = new Key(hex.DecodeData("0606060606060606060606060606060606060606060606060606060606060606"))
  let privKey7 = new Key(hex.DecodeData("0707070707070707070707070707070707070707070707070707070707070707"))
  let pubkey1 = privKey1.PubKey
  let pubkey2 = privKey2.PubKey
  let pubkey3 = privKey3.PubKey
  let pubkey4 = privKey4.PubKey
  let pubkey5 = privKey5.PubKey
  let pubkey6 = privKey6.PubKey
  let pubkey7 = privKey7.PubKey
  type BlockWithHeight with
    member this.CreateNext(addr: BitcoinAddress) =
      let nextHeight = this.Height + BlockHeightOffset16.One
      {
        Block = this.Block.CreateNextBlockWithCoinbase(addr, nextHeight.Value |> int)
        Height = nextHeight
      }

    member this.CreateNextMany (network: Network) (num: int) =
      let mutable b = this.Copy()
      [
        for i in 1..num do
          let addr = (new Key()).PubKey.WitHash.GetAddress(network)
          let n = b.CreateNext(addr)
          yield n
          b <- n
      ]


  type LoopOut with
    member this.GetGenesis() = {
      Block = this.BaseAssetNetwork.GetGenesis()
      Height = BlockHeight.Zero
    }
    member this.Normalize() =
      { this with
          Id = SwapId(Guid.NewGuid().ToString())
          ChainName = Network.RegTest.ChainName.ToString()
          IsOffchainOfferResolved = false
          IsClaimTxConfirmed = false
          ClaimTransactionId = None
          SwapTxHex = None
          MaxMinerFee = Money.Coins(10m)
          OnChainAmount = Money.Max(this.OnChainAmount, Money.Satoshis(10000m))
          SwapTxHeight = None
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
    member this.GetGenesis() = {
      Block = this.QuoteAssetNetwork.GetGenesis()
      Height = BlockHeight.Zero
    }
    member this.Normalize() =
      { this with
          Id = SwapId(Guid.NewGuid().ToString())
          ChainName = Network.RegTest.ChainName.ToString()
          SwapTxInfoHex = None
          RefundTransactionId = None
          MaxMinerFee = Money.Coins(10m)
        }

  let testLoopOut1 =
      let claimKey = new Key()
      let b = SupportedCryptoCode.LTC
      let q = SupportedCryptoCode.BTC
      let baseN = Altcoins.Litecoin.Instance.Regtest
      let quoteN = Network.RegTest
      let claimAddr =
        claimKey.PubKey.WitHash.GetAddress(baseN)
      let paymentPreimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
      let paymentHash = paymentPreimage.Hash
      let refundKey = new Key()
      let timeout = BlockHeight(30u)
      let redeemScript =
        Scripts.reverseSwapScriptV1(paymentHash) claimKey.PubKey refundKey.PubKey timeout
      let invoice =
        let fields = { TaggedFields.Fields = [ PaymentHashTaggedField paymentHash; DescriptionTaggedField "test" ] }
        PaymentRequest.TryCreate(quoteN, Some(LNMoney.Satoshis(100000L)), DateTimeOffset.UtcNow, fields, new Key())
        |> ResultUtils.Result.deref
      let onChainAmount = Money.Coins(30m)
      {
          Id = SwapId(Guid.NewGuid().ToString())
          ChainName = Network.RegTest.ChainName.ToString()
          IsOffchainOfferResolved = false
          IsClaimTxConfirmed = false
          ClaimTransactionId = None
          SwapTxHex = None
          MaxMinerFee = Money.Coins(10m)
          OnChainAmount = onChainAmount
          SwapTxHeight = None
          Preimage = paymentPreimage
          TimeoutBlockHeight = timeout
          Invoice = invoice.ToString()
          PrepayInvoice = getDummyTestInvoice(quoteN)
          ClaimKey = claimKey
          RedeemScript = redeemScript
          SwapTxConfRequirement =
            BlockHeightOffset32(3u)
          ClaimAddress = claimAddr.ToString();
          OutgoingChanIds = [||]
          PairId = PairId(b, q)
          Label = "test"
          SweepConfTarget = BlockHeightOffset32(6u)
          Cost = SwapCost.Zero
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
        PairId = PairId(SupportedCryptoCode.LTC, SupportedCryptoCode.BTC)
        SwapTxHeight = None
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
    let loopOut = { loopOut.Normalize() with PairId = PairId (baseAsset, SupportedCryptoCode.BTC) }

    let paymentPreimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
    let timeoutBlockHeight = BlockHeight(30u)
    let onChainAmount = Money.Max(loopOut.OnChainAmount, Money.Satoshis(100000m))
    let loopOut =
      loopOut.Sanitize(paymentPreimage, timeoutBlockHeight, onChainAmount, acceptZeroConf)
    let txBroadcasted = ResizeArray()
    let mutable i = 0
    let repo = getTestRepository()
    let commandsToEvents (commands: Swap.Command list) =
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
      commands
      |> List.map(fun c ->
        i <- i + 1
        (DateTime(2001, 01, 30, i, 0, 0), c) ||> getCommand
      )
      |> commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB
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
    let _ =
      let events =
        [
          let loopOutParams = {
            loopOutParams with
              Swap.LoopOutParams.Height = BlockHeight.Zero
          }
          (Swap.Command.NewLoopOut(loopOutParams, loopOut))
          (Swap.Command.CommitSwapTxInfoFromCounterParty(swapTx.ToHex()))
        ]

        |> commandsToEvents
      Assert.Contains(Swap.Event.TheirSwapTxPublished(swapTx.ToHex()), events |> Result.deref |> List.map(fun e -> e.Data))

      let lastEvent =
        events |> getLastEvent
      let expected =
        if acceptZeroConf then
          Swap.Event.ClaimTxPublished(null).Type
        else
          Swap.Event.TheirSwapTxPublished(null).Type
      Assert.Equal(expected, lastEvent.Data.Type)

    let genesis =
      loopOut.GetGenesis()
    let _ =
      let block =
        genesis.CreateNext(pubkey1.WitHash.GetAddress(loopOut.BaseAssetNetwork))
      block.Block.AddTransaction(swapTx) |> ignore
      // first confirmation
      let events =
        [
          (Swap.Command.NewBlock(block, baseAsset))
        ]
        |> commandsToEvents
      Assert.Contains(Swap.Event.TheirSwapTxConfirmedFirstTime(block.Block.Header.GetHash()),
                      events |> Result.deref |> List.map(fun e -> e.Data))
      if acceptZeroConf then
        let lastEvent = events |> getLastEvent
        let expected = Swap.Event.ClaimTxPublished(null).Type
        Assert.Equal(expected, lastEvent.Data.Type)

    let genesis =
      {
        Block = loopOut.BaseAssetNetwork.GetGenesis()
        Height = BlockHeight.Zero
      }

    let b1 =
      genesis.CreateNext(pubkey1.WitHash.GetAddress(loopOut.BaseAssetNetwork))
    let b2 =
      b1.CreateNext(pubkey2.WitHash.GetAddress(loopOut.BaseAssetNetwork))
    let b3 =
      b2.CreateNext(pubkey3.WitHash.GetAddress(loopOut.BaseAssetNetwork))
    let _ =
      // confirm until our claim tx gets published for sure.
      let commands =
        [
          (Swap.Command.NewBlock(b1, baseAsset))
          (Swap.Command.NewBlock(b2, baseAsset))
          (Swap.Command.NewBlock(b3, baseAsset))
        ]
      let events =
        commandsToEvents commands
        |> Result.deref
      if not <| acceptZeroConf then
        let expected =
          Swap.Event.ClaimTxPublished(null).Type
        Assert.Contains(expected, events |> List.map(fun e -> e.Data.Type))

    let claimTx =
      txBroadcasted |> Seq.last
    let b4 =
      b3.CreateNext(pubkey4.WitHash.GetAddress(loopOut.BaseAssetNetwork))
    b4.Block.AddTransaction(claimTx) |> ignore
    let lastEvent =
      [
        (Swap.Command.NewBlock(b4, baseAsset))
      ]
      |> commandsToEvents
      |> getLastEvent
    let serverFee = LNMoney.Satoshis(1000L)
    let sweepAmount = loopOut.OnChainAmount - serverFee.ToMoney()
    let expected =
      let sweepTxId = txBroadcasted |> Seq.last |> fun t -> t.GetHash()
      Swap.Event.ClaimTxConfirmed(b4.Block.Header.GetHash(), sweepTxId, sweepAmount)
    Assert.Equal(expected.Type, lastEvent.Data.Type)
    Assert.True(Money.Zero < sweepAmount && sweepAmount < loopOut.OnChainAmount)
    let lastEvent =
      [
        let r =
          let routingFee = LNMoney.Satoshis(100L)
          { Swap.PayInvoiceResult.AmountPayed = onChainAmount.ToLNMoney() + serverFee
            Swap.PayInvoiceResult.RoutingFee = routingFee }
        (Swap.Command.OffChainOfferResolve(r))
      ]
      |> commandsToEvents
      |> getLastEvent
    Assert.Equal(Swap.Event.FinishedSuccessfully(loopOut.Id), lastEvent.Data)

  /// It should time out when the server does not tell us about swap tx that they ought to published
  /// after we made an offer.
  [<Property(MaxTest=10)>]
  member this.TestLoopOut_Timeout(loopOutParams: Swap.LoopOutParams, acceptZeroConf) =
    let repo = getTestRepository()
    let loopOut = testLoopOut1
    let mutable i = 0
    let commandsToEvents (commands: Swap.Command list) =
      let deps =
        mockDeps()
      commands
      |> List.map(fun c ->
        i <- i + 1
        let d = DateTime(2001, 01, 30, 0, 0, 0) + TimeSpan.FromMilliseconds(i |> float)
        (d, c) ||> getCommand
      )
      |> commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB
    let RunAndAssertFinishedByTimeout (loopOut: LoopOut) cmd =
      let lastEvent =
        (Swap.Command.NewLoopOut(loopOutParams, loopOut)) :: cmd
        |> commandsToEvents
        |> getLastEvent
      Assert.Equal(Swap.Event.FinishedByTimeout(loopOut.Id, "foo").Type, lastEvent.Data.Type)

    let currentHeight = loopOutParams.Height
    let confirmCommandsUntilTimeout (loopOut: LoopOut) =
      let generator =
        Seq.unfold(fun (b: BlockWithHeight) ->
          let addr = (new Key()).PubKey.WitHash.GetAddress(loopOut.BaseAssetNetwork)
          let next =  b.CreateNext(addr)
          (Swap.Command.NewBlock(b, loopOut.PairId.Base), next) |> Some
          )
          (loopOut.GetGenesis())
      generator
      |> Seq.skip(currentHeight.Value |> int)
      |> Seq.take(loopOut.TimeoutBlockHeight.Value |> int)
      |> Seq.toList

    // case 1: nothing happens after creation.
    let _ =
      let loopOut = {
        loopOut with
          Invoice = getDummyTestInvoice(loopOut.QuoteAssetNetwork)
          PrepayInvoice = getDummyTestInvoice(loopOut.QuoteAssetNetwork)
          TimeoutBlockHeight = BlockHeight(30u)
          Id = (Guid.NewGuid()).ToString() |> SwapId
      }
      confirmCommandsUntilTimeout loopOut |> RunAndAssertFinishedByTimeout loopOut
    // case 2: nothing happens after they tell us about the swap tx.
    let _ =
      let loopOut =
        let paymentPreimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
        let timeoutBlockHeight = BlockHeight(30u)
        let onChainAmount = Money.Max(loopOut.OnChainAmount, Money.Satoshis(100000m))
        {
          loopOut.Sanitize(paymentPreimage, timeoutBlockHeight, onChainAmount, acceptZeroConf)
          with
            Id = (Guid.NewGuid()).ToString() |> SwapId
        }
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
          (Swap.Command.CommitSwapTxInfoFromCounterParty(swapTx.ToHex()))
        ]
      (commands @ confirmCommandsUntilTimeout loopOut)|> RunAndAssertFinishedByTimeout loopOut
    ()

  (*
  [<Property(MaxTest=10)>]
  member this.TestLoopOut_Reorg(loopOut: LoopOut, loopOutParams: Swap.LoopOutParams, testAltcoin, acceptZeroConf: bool) =
    let baseAsset =
       if testAltcoin then SupportedCryptoCode.LTC else SupportedCryptoCode.BTC
    let loopOut =
      let quoteAsset = SupportedCryptoCode.BTC
      { loopOut.Normalize() with PairId = PairId(baseAsset, quoteAsset) }
    let initialBlockHeight = BlockHeight.One
    let timeoutBlockHeight = initialBlockHeight + BlockHeightOffset16(30us)
    let paymentPreimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
    let onChainAmount = Money.Max(loopOut.OnChainAmount, Money.Satoshis(100000m))
    let loopOut = {
      loopOut.Sanitize(paymentPreimage, timeoutBlockHeight, onChainAmount, acceptZeroConf)
        with
        TimeoutBlockHeight = timeoutBlockHeight
    }
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
    let b0 =
      let b = loopOut.GetGenesis()
      b

    let b1_1 =
      let b = b0.CreateNext(pubkey1.WitHash.GetAddress(loopOut.BaseAssetNetwork))
      b.Block.AddTransaction(swapTx) |> ignore
      b
    let b1_2 =
      let b = b0.CreateNext(pubkey2.WitHash.GetAddress(loopOut.BaseAssetNetwork))
      b.Block.AddTransaction(swapTx) |> ignore
      b

    let confirmationCommands =
      [
        let loopOutParams = { loopOutParams with Height = initialBlockHeight }
        (DateTime(2001, 01, 30, 1, 0, 0), Swap.Command.NewLoopOut(loopOutParams, loopOut))
        (DateTime(2001, 01, 30, 2, 0, 0), Swap.Command.CommitSwapTxInfoFromCounterParty(swapTx.ToHex()))
        (DateTime(2001, 01, 30, 3, 0, 0), Swap.Command.NewBlock(b0, baseAsset))
        (DateTime(2001, 01, 30, 4, 0, 0), Swap.Command.NewBlock(b1_1, baseAsset))
      ]

    let deps = mockDeps()
    let repo = getTestRepository()
    // 1-block reorg
    let _ =
      let loopOut = { loopOut with Id = (Guid.NewGuid()).ToString() |> SwapId }
      let confirmationBlockHash = b1_1.Block.Header.GetHash()
      let commands =
        confirmationCommands @ [
          (DateTime(2001, 01, 30, 5, 0, 0), Swap.Command.UnConfirmBlock(confirmationBlockHash))
        ]
        |> List.map(fun x -> x ||> getCommand)
      let lastEvent =
        commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB commands
        |> getLastEvent
      // must emit an reorg event
      Assert.Equal(Swap.Event.BlockUnConfirmed(confirmationBlockHash), lastEvent.Data)

      let confirmationBlockHash = b1_2.Block.Header.GetHash()
      let additionalCommands =
        [
          // new confirmation of the swap tx...
          (DateTime(2001, 01, 30, 6, 0, 0), Swap.Command.NewBlock(b1_2, baseAsset))
        ]
        |> List.map(fun x -> x ||> getCommand)
      let events =
        commandsToEvents assureRunSynchronously deps repo loopOut.Id useRealDB additionalCommands
      // must be detected.
      Assert.Contains(Swap.Event.TheirSwapTxConfirmedFirstTime(confirmationBlockHash), events |> Result.deref |> List.map(fun e -> e.Data))

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
    *)
    ()

  [<Property(MaxTest=10)>]
  member this.TestLoopIn_Timeout(loopIn: LoopIn, testAltcoin: bool) =
    /// prepare
    let quoteAsset =
      if testAltcoin then SupportedCryptoCode.LTC else SupportedCryptoCode.BTC
    let baseAsset = SupportedCryptoCode.BTC
    let loopIn =
      { loopIn.Normalize() with
          PairId = PairId (baseAsset, quoteAsset)
        }
    let initialBlockHeight = BlockHeight.Zero
    let timeoutBlockHeight = initialBlockHeight + BlockHeightOffset16(3us)
    use key = new Key()
    let repo = getTestRepository()
    let txBroadcasted = ResizeArray()
    use fundsKey = new Key()
    let mutable i = 0
    let commandsToEvents (commands: Swap.Command list) =
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
      commands
      |> List.map(fun c ->
        i <- i + 1
        (DateTime(2001, 01, 30, i, 0, 0), c) ||> getCommand
      )
      |> commandsToEvents assureRunSynchronously deps repo loopIn.Id useRealDB

    // act
    let events =
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
        Swap.Command.NewLoopIn(initialBlockHeight, loopIn)
      ]
      |> commandsToEvents

    // assert
    Assertion.isOk events
    let swapTx = Assert.Single(txBroadcasted)
    let lastEvent = events |> getLastEvent
    Assert.Equal(Swap.Event.OurSwapTxPublished(Money.Zero, swapTx.ToHex(), 0u).Type, lastEvent.Data.Type)

    // act
    let b0 =
      let b = loopIn.GetGenesis()
      b.Block.AddTransaction(swapTx) |> ignore
      b
    let b1 = b0.CreateNext(pubkey1.WitHash.GetAddress(loopIn.QuoteAssetNetwork))
    let b2 = b1.CreateNext(pubkey2.WitHash.GetAddress(loopIn.QuoteAssetNetwork))
    let e2 =
      [
        Swap.Command.NewBlock(b0, quoteAsset)
        Swap.Command.NewBlock(b1, quoteAsset)
        Swap.Command.NewBlock(b2, quoteAsset)
      ]
      |> commandsToEvents
    // assert
    Assert.Contains(e2 |> Result.deref,
                    fun e -> e.Data.Type = Swap.Event.OurSwapTxConfirmed(b0.Block.Header.GetHash(), uint256.Zero, 0u).Type)
    let lastEvent = e2 |> getLastEvent
    Assert.Equal(Swap.Event.RefundTxPublished(uint256.Zero).Type, lastEvent.Data.Type)

    let b3 =
      let b = b2.CreateNext(pubkey3.WitHash.GetAddress(loopIn.QuoteAssetNetwork))
      let refundTx =
        let refundTxId = lastEvent.Data |> function | Swap.Event.RefundTxPublished refundTxId -> refundTxId | _ -> failwith "unreachable"
        txBroadcasted |> Seq.find(fun tx -> tx.GetHash() = refundTxId)
      b.Block.AddTransaction(refundTx) |> ignore
      b

    // act
    let lastEvent =
      [
        (Swap.Command.NewBlock(b3, quoteAsset))
      ]
      |> commandsToEvents
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
          PairId = PairId(baseAsset, quoteAsset)
        }
    use key = new Key()
    let repo = getTestRepository()
    let txBroadcasted = ResizeArray()
    use fundsKey = new Key()
    let initialBlockHeight = BlockHeight.Zero
    let timeoutBlockHeight = initialBlockHeight + BlockHeightOffset16(2us)
    let mutable i = 0
    let commandsToEvents (commands: Swap.Command list) =
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

      commands
      |> List.map(fun c ->
        i <- i + 1
        (DateTime(2001, 01, 30, i, 0, 0), c) ||> getCommand
      )
      |> commandsToEvents assureRunSynchronously deps repo loopIn.Id useRealDB
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
      [
        (Swap.Command.NewLoopIn(initialBlockHeight, loopIn))
      ]
      |> commandsToEvents

    // assert
    Assertion.isOk events
    let swapTx = Assert.Single(txBroadcasted) // swap tx and refund tx
    let lastEvent = events |> getLastEvent
    Assert.Equal(Swap.Event.OurSwapTxPublished(Money.Zero, swapTx.ToHex(), 0u).Type, lastEvent.Data.Type)

    let b0 =
      let b = loopIn.GetGenesis()
      b.Block.AddTransaction(swapTx) |> ignore
      b
    let b1 =
      let b = b0.CreateNext(pubkey1.WitHash.GetAddress(loopIn.QuoteAssetNetwork))
      let dummySuccessTx =
        let txb = loopIn.QuoteAssetNetwork.CreateTransactionBuilder()
        txb
          .AddCoins(swapTx)
          .AddKnownRedeems(loopIn.RedeemScript)
          .SendAll(new Key())
          .BuildTransaction(false)
      b.Block.AddTransaction(dummySuccessTx) |> ignore
      b
    let amt = Money.Coins(1m)
    // act
    let lastEvent =
      let l1 = (Swap.Command.NewBlock(b0, quoteAsset))
      let l2 =
        [
          (Swap.Command.NewBlock(b1, quoteAsset))
          (Swap.Command.CommitReceivedOffChainPayment(amt))
        ]
        |> List.shuffle
      l1 :: l2
      |> commandsToEvents
      |> getLastEvent

    // assert
    Assert.Equal(Swap.Event.FinishedSuccessfully(loopIn.Id).Type, lastEvent.Data.Type)

