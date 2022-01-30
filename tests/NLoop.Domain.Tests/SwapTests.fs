module SwapTests

open DotNetLightning.Payment
open DotNetLightning.Utils
open NBitcoin.DataEncoders
open NLoop.Domain
open NLoop.Tests
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

  type LoopOut with
    member this.GetGenesis() = {
      Block = this.BaseAssetNetwork.GetGenesis()
      Height = BlockHeight.Zero
    }
    member loopOut.GetDummySwapTx(pubkey: PubKey) =
      let fee = Money.Satoshis(30m)
      let txb =
        loopOut
          .BaseAssetNetwork
          .CreateTransactionBuilder()
      txb
        .AddRandomFunds(loopOut.OnChainAmount + fee + Money.Coins(1m))
        .Send(loopOut.RedeemScript.WitHash.ScriptPubKey, loopOut.OnChainAmount)
        .SendFees(fee)
        .SetChange(pubkey.WitHash)
        .BuildTransaction(true)

    member loopOut.GetDummySwapTx() =
      loopOut.GetDummySwapTx(pubkey7)

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

  let testLoopOutParams = {
    Swap.LoopOutParams.Height = BlockHeight.Zero
    Swap.LoopOutParams.MaxPrepayFee = Money.Satoshis(100m)
    Swap.LoopOutParams.MaxPaymentFee = Money.Satoshis(1000m)
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
      (new Key()).PubKey.WitHash.GetAddress(Network.RegTest)
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
      Swap.Deps.PayInvoiceImmediate =
        fun _n _parameters req ->
          let r = {
            Swap.PayInvoiceResult.AmountPayed = req.AmountValue |> Option.defaultValue(LNMoney.Satoshis(100000L))
            Swap.PayInvoiceResult.RoutingFee = LNMoney.Satoshis(10L)
          }
          Task.FromResult(r)
      Swap.Deps.Offer = fun _ _ _ -> Task.FromResult (Ok ())
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

  let getHandler deps store =
    let aggr = Swap.getAggregate deps
    Swap.getHandler aggr store

  let executeCommand handler swapId useRealDB =
    fun cmd -> taskResult {
      let! events = handler.Execute swapId cmd
      do! Async.Sleep 10
      return events
    }

  let commandsToEventsCore assureRunSequentially handler swapId useRealDB commands =
    if assureRunSequentially then
      commands
      |> List.map(executeCommand handler swapId useRealDB >> fun t -> t.GetAwaiter().GetResult())
      |> List.sequenceResultM
      |> Result.map(List.concat)
    else
      commands
      |> List.map(executeCommand handler swapId useRealDB)
      |> List.sequenceTaskResultM
      |> TaskResult.map(List.concat)
      |> fun t -> t.GetAwaiter().GetResult()
  let commandsToEvents assureRunSequentially deps store swapId useRealDB commands =
    let handler = getHandler deps store
    commandsToEventsCore assureRunSequentially handler swapId useRealDB commands

  let commandsToState assureRunSequentially deps store swapId useRealDB commands =
    let handler = getHandler deps store
    // we discard the newly generated events. and re-load the events from the repository
    // and reconstitute the state again entirely from the events in the repository.
    let _newEvents = commandsToEventsCore assureRunSequentially handler swapId useRealDB commands
    let recordedEvents =
      let e = commands |> List.last |> fun c -> c.Meta.EffectiveDate
      handler.Replay swapId (AsAt e)
    recordedEvents.GetAwaiter().GetResult()
    |> Result.map handler.Reconstitute

  let assertNotUnknownEvent (e: ESEvent<_>) =
    e.Data |> function | Swap.Event.UnknownTagEvent { Tag = t } -> failwith $"unknown tag {t}" | _ -> e
  let getLastEvent e =
      e
      |> Result.deref
      |> List.map(assertNotUnknownEvent)
      |> List.last

  [<Fact>]
  member this.JsonSerializerTest() =
    let events = [
      Swap.Event.FinishedByError { Id = SwapId("foo"); Error = "Error msg" }
      Swap.Event.ClaimTxPublished { Txid = uint256.Zero }
      Swap.Event.TheirSwapTxPublished { TxHex = Network.RegTest.CreateTransaction().ToHex() }
    ]

    for e in events do
      let ser = Swap.serializer
      let e2 = ser.EventToBytes(e) |> ser.BytesToEvents
      Assertion.isOk(e2)

  [<Property(MaxTest=10)>]
  member this.JsonSerializerTest_LoopIn(loopIn: LoopIn, height: uint32) =
    let e = Swap.Event.NewLoopInAdded { Height = height |> BlockHeight; LoopIn = loopIn }
    let ser = Swap.serializer
    let e2 = ser.EventToBytes(e) |> ser.BytesToEvents
    Assertion.isOk(e2)

  [<Property(MaxTest=10)>]
  member this.JsonSerializerTest_LoopOut(loopOut: LoopOut, height: uint32) =
    let e = Swap.Event.NewLoopOutAdded { Height = height |> BlockHeight; LoopOut = loopOut }
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

  [<Property(MaxTest=200)>]
  member this.SwapEventSerialize(e: Swap.Event) =
    match e with
    | Swap.Event.UnknownTagEvent { Tag = t } when Swap.Event.KnownTags |> Array.contains t ->
      ()
    | _ ->
    let ser = Swap.serializer
    let b = ser.EventToBytes e |> ser.BytesToEvents
    Assertion.isOk(b)
    let e2 = b |> Result.deref
    Assert.Equal(e, e2)

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
      let store = InMemoryStore.getEventStore()
      commandsToEvents assureRunSynchronously deps store loopOut.Id useRealDB commands
    Assertion.isOk events

  static member TestLoopOut_SuccessTestData =
    seq [
      ("basic case", testLoopOut1, testLoopOutParams)
      let loopOut = { testLoopOut1 with PairId = PairId (SupportedCryptoCode.LTC, SupportedCryptoCode.BTC) }
      ("test altcoin", loopOut, testLoopOutParams)
      let loopOut = {testLoopOut1 with SwapTxConfRequirement = BlockHeightOffset32.Zero }
      ("accept zero conf", loopOut, testLoopOutParams)
      ()
    ]
    |> Seq.map(fun (name, loopOut, loopOutParams) -> [|
      name |> box
      loopOut |> box
      loopOutParams |> box
    |])

  [<Theory>]
  [<MemberData(nameof(SwapDomainTests.TestLoopOut_SuccessTestData))>]
  member this.TestLoopOut_Success(name, loopOut: LoopOut, loopOutParams: Swap.LoopOutParams) =
    let paymentPreimage = PaymentPreimage.Create(RandomUtils.GetBytes 32)
    let loopOut = { loopOut with Preimage = paymentPreimage }
    let txBroadcasted = ResizeArray()
    let mutable i = 0
    let store = InMemoryStore.getEventStore()
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
      |> commandsToEvents assureRunSynchronously deps store loopOut.Id useRealDB
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
      Assert.Contains(Swap.Event.TheirSwapTxPublished { TxHex = swapTx.ToHex() }, events |> Result.deref |> List.map(fun e -> e.Data))

      let lastEvent =
        events |> getLastEvent
      let expected =
        if loopOut.AcceptZeroConf then
          Swap.Event.ClaimTxPublished({ Txid = null }).Type
        else
          Swap.Event.TheirSwapTxPublished({ TxHex = null }).Type
      Assert.Equal(expected, lastEvent.Data.Type)

    let genesis =
      loopOut.GetGenesis()
    let b1 =
      genesis.CreateNext(pubkey1.WitHash.GetAddress(loopOut.BaseAssetNetwork))
    b1.Block.AddTransaction(swapTx) |> ignore
    let _ =
      // first confirmation
      let events =
        [
          (Swap.Command.NewBlock(b1, loopOut.PairId.Base))
        ]
        |> commandsToEvents
      Assert.Contains(Swap.Event.TheirSwapTxConfirmedFirstTime({ BlockHash = b1.Block.Header.GetHash(); Height = b1.Height }),
                      events |> Result.deref |> List.map(fun e -> e.Data))
      if loopOut.AcceptZeroConf then
        let lastEvent = events |> getLastEvent
        let expected = Swap.Event.ClaimTxPublished({ Txid = null }).Type
        Assert.Equal(expected, lastEvent.Data.Type)

    let b2 =
      b1.CreateNext(pubkey2.WitHash.GetAddress(loopOut.BaseAssetNetwork))
    let b3 =
      b2.CreateNext(pubkey3.WitHash.GetAddress(loopOut.BaseAssetNetwork))
    let _ =
      // confirm until our claim tx gets published for sure.
      let events =
        [
          (Swap.Command.NewBlock(b2, loopOut.PairId.Base))
          (Swap.Command.NewBlock(b3, loopOut.PairId.Base))
        ]
        |> commandsToEvents
        |> Result.deref

      let expected =
        Swap.Event.ClaimTxPublished({ Txid = null }).Type
      Assert.Contains(expected, events |> List.map(fun e -> e.Data.Type))

    let claimTx =
      txBroadcasted |> Seq.last
    let b4 =
      b3.CreateNext(pubkey4.WitHash.GetAddress(loopOut.BaseAssetNetwork))
    b4.Block.AddTransaction(claimTx) |> ignore
    let lastEvent =
      [
        (Swap.Command.NewBlock(b4, loopOut.PairId.Base))
      ]
      |> commandsToEvents
      |> getLastEvent
    let serverFee = LNMoney.Satoshis(1000L)
    let sweepAmount = loopOut.OnChainAmount - serverFee.ToMoney()
    let expected =
      let sweepTxId = txBroadcasted |> Seq.last |> fun t -> t.GetHash()
      Swap.Event.ClaimTxConfirmed { BlockHash = b4.Block.Header.GetHash(); TxId = sweepTxId; SweepAmount = sweepAmount }
    Assert.Equal(expected.Type, lastEvent.Data.Type)
    Assert.True(Money.Zero < sweepAmount && sweepAmount < loopOut.OnChainAmount)
    let lastEvent =
      [
        let r =
          let routingFee = LNMoney.Satoshis(100L)
          { Swap.PayInvoiceResult.AmountPayed = loopOut.OnChainAmount.ToLNMoney() + serverFee
            Swap.PayInvoiceResult.RoutingFee = routingFee }
        (Swap.Command.OffChainOfferResolve(r))
      ]
      |> commandsToEvents
      |> getLastEvent
    Assert.Equal(Swap.Event.FinishedSuccessfully { Id = loopOut.Id }, lastEvent.Data)

  static member TestLoopOut_Reorg_TestData =
    let loopOut = testLoopOut1
    let cc = loopOut.PairId.Base
    // b0 ---> b1_1 ---> b2_1
    //   \
    //    ---> b1_2 ---> b2_2
    let b0 =
       loopOut.GetGenesis()
    let b1_1 =
      b0.CreateNext(pubkey1.WitHash.GetAddress(loopOut.BaseAssetNetwork))
    let b1_2 =
      b0.CreateNext(pubkey2.WitHash.GetAddress(loopOut.BaseAssetNetwork))
    let b2_1 =
      b1_1.CreateNext(pubkey3.WitHash.GetAddress(loopOut.BaseAssetNetwork))
    let b2_2 =
      b1_2.CreateNext(pubkey4.WitHash.GetAddress(loopOut.BaseAssetNetwork))

    let swapTx = loopOut.GetDummySwapTx()
    let addSwap (b: BlockWithHeight) =
      let bCopy = b.Copy()
      bCopy.Block.AddTransaction(swapTx) |> ignore
      bCopy
    let b1_1t, b1_2t, b2_1t, b2_2t = (addSwap b1_1, addSwap b1_2, addSwap b2_1, addSwap b2_2)
    let newB b = Swap.Command.NewBlock(b, cc)
    let unB b = Swap.Command.UnConfirmBlock(b.Block.Header.GetHash())
    seq [
      ("Confirmed once", [ newB b1_1t ], 20, Some 1)
      let cmds = [newB b1_1t; newB b1_2t]
      ("Confirmed twice in the competing blocks", cmds, 20, Some 1)
      let cmds = [newB b1_1t; newB b1_2; newB b2_2t]
      ("Confirmed again in longer branch", cmds, 20, Some 1)
      let cmds = [newB b1_1t; newB b1_2; unB b1_1t; newB b2_2t]
      ("Confirmed again in longer branch (un-confirm the old one)", cmds, 20, Some 2)
    ]
    |> Seq.map(fun (name, inputCmds, confRequirement, expectedSwapHeight) ->
      [|
        name |> box
        newB b0 :: inputCmds |> box
        confRequirement |> uint |> BlockHeightOffset32 |> box
        swapTx |> box
        expectedSwapHeight |> Option.map(uint >> BlockHeight) |> box
      |]
    )

  [<Theory>]
  [<MemberData(nameof(SwapDomainTests.TestLoopOut_Reorg_TestData))>]
  member this.TestLoopOut_Reorg(name: string,
                                inputCmds: Swap.Command list,
                                confRequirement: BlockHeightOffset32,
                                swapTx: Transaction,
                                expectedSwapHeight: BlockHeight option) =
    let initialBlockHeight = BlockHeight.Zero
    let timeoutBlockHeight = initialBlockHeight + BlockHeightOffset16(30us)
    let loopOut = {
        testLoopOut1
        with
          Id = (Guid.NewGuid()).ToString() |> SwapId
          TimeoutBlockHeight = timeoutBlockHeight
          SwapTxConfRequirement = confRequirement
    }
    let mutable i = 0
    let store = InMemoryStore.getEventStore()
    let commandsToState (commands: Swap.Command list) =
      let deps =
        mockDeps()
      commands
      |> List.map(fun c ->
        i <- i + 1
        (DateTime(2001, 01, 30, i, 0, 0), c) ||> getCommand
      )
      |> commandsToState assureRunSynchronously deps store loopOut.Id useRealDB

    let confirmationCommands =
      [
        let loopOutParams = { testLoopOutParams with Height = initialBlockHeight }
        (Swap.Command.NewLoopOut(loopOutParams, loopOut))
        (Swap.Command.CommitSwapTxInfoFromCounterParty(swapTx.ToHex()))
      ]

    let height, finalState =
      confirmationCommands @ inputCmds
      |> commandsToState
      |> Result.deref
      |> function | Swap.State.Out(h, o) -> (h, o) | x -> failwith $"{name}: Unexpected state {x}"
    Assert.Equal(expectedSwapHeight, finalState.SwapTxHeight)

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
    let timeoutBlockHeight = initialBlockHeight + BlockHeightOffset16(2us)
    use key = new Key()
    let store = InMemoryStore.getEventStore()
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
      |> commandsToEvents assureRunSynchronously deps store loopIn.Id useRealDB

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
    Assert.Equal(Swap.Event.OurSwapTxPublished({ Fee = Money.Zero; TxHex = swapTx.ToHex(); HtlcOutIndex = 0u }).Type, lastEvent.Data.Type)

    // act
    let b1 =
      let b = loopIn.GetGenesis()
      b.Block.AddTransaction(swapTx) |> ignore
      b
    let b2 = b1.CreateNext(pubkey1.WitHash.GetAddress(loopIn.QuoteAssetNetwork))
    let b3 = b2.CreateNext(pubkey2.WitHash.GetAddress(loopIn.QuoteAssetNetwork))
    let e2 =
      [
        Swap.Command.NewBlock(b1, quoteAsset)
        Swap.Command.NewBlock(b2, quoteAsset)
        Swap.Command.NewBlock(b3, quoteAsset)
      ]
      |> commandsToEvents
    // assert
    Assert.Contains(e2 |> Result.deref,
                    fun e -> e.Data.Type = (Swap.Event.OurSwapTxConfirmed { BlockHash = b1.Block.Header.GetHash(); TxId = uint256.Zero; HTlcOutIndex = 0u }).Type)
    let lastEvent = e2 |> getLastEvent
    Assert.Equal(Swap.Event.RefundTxPublished({ TxId = uint256.Zero }).Type, lastEvent.Data.Type)

    let b3 =
      let b = b2.CreateNext(pubkey3.WitHash.GetAddress(loopIn.QuoteAssetNetwork))
      let refundTx =
        let refundTxId = lastEvent.Data |> function | Swap.Event.RefundTxPublished { TxId = id } -> id | _ -> failwith "unreachable"
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
    Assert.Equal(Swap.Event.FinishedByRefund({ Id = loopIn.Id }).Type, lastEvent.Data.Type)

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
    let store = InMemoryStore.getEventStore()
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
      |> commandsToEvents assureRunSynchronously deps store loopIn.Id useRealDB
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
    Assert.Equal(Swap.Event.OurSwapTxPublished({ Fee = Money.Zero; TxHex = swapTx.ToHex(); HtlcOutIndex = 0u }).Type,
                 lastEvent.Data.Type)

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
    Assert.Equal(Swap.Event.FinishedSuccessfully( { Id = loopIn.Id }).Type, lastEvent.Data.Type)

