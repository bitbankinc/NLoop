module Generators

open System
open DotNetLightning.Payment
open DotNetLightning.Utils
open DotNetLightning.Utils.Primitives
open FsCheck
open NBitcoin
open NBitcoin.Altcoins
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Server.DTOs
open NLoop.Domain


[<AutoOpen>]
module Helpers =
  let byteGen = byte <!> Gen.choose(0, 127)
  let bytesGen = Gen.listOf(byteGen) |> Gen.map(List.toArray)
  let bytesOfNGen(n) = Gen.listOfLength n byteGen |> Gen.map(List.toArray)
  let uint256Gen = bytesOfNGen(32) |> Gen.map(fun bs -> uint256(bs))
  let moneyGen = Arb.generate<uint64> |> Gen.map(Money.Satoshis)

  let shortChannelIdGen = Arb.generate<uint64> |> Gen.map(ShortChannelId.FromUInt64)
  let keyGen = Gen.fresh (fun () -> new Key())

  let pubKeyGen = gen {
      let! key = keyGen
      return key.PubKey
  }

  // scripts

  let pushOnlyOpcodeGen = bytesOfNGen(4) |> Gen.map(Op.GetPushOp)
  let pushOnlyOpcodesGen = Gen.listOf pushOnlyOpcodeGen

  let pushScriptGen = Gen.nonEmptyListOf pushOnlyOpcodeGen |> Gen.map(fun ops -> Script(ops))

  let networkGen =
    Gen.oneof [
      // Gen.constant Network.Main
      // Gen.constant Network.TestNet
      Gen.constant Network.RegTest
    ]

  let bitcoinWitScriptAddressGen =
    gen {
      let! n = networkGen
      let! sc = pushScriptGen
      return sc.WitHash.GetAddress(n)
    }

  let bitcoinWitPubKeyAddressGen =
    gen {
      let! pk = pubKeyGen
      let! n = networkGen
      return pk.WitHash.GetAddress(n)
    }

  let bitcoinAddressGen =
    Gen.oneof [
      bitcoinWitScriptAddressGen |> Gen.map(unbox)
      bitcoinWitPubKeyAddressGen |> Gen.map(unbox)
    ]
  let nodeIdGen =
    pubKeyGen |> Gen.map(NodeId)

  let networkSetGen =
    Gen.oneof [
          Gen.constant Bitcoin.Instance |> Gen.map(unbox)
          Gen.constant Litecoin.Instance |> Gen.map(unbox)
        ]

  let outpointGen = gen {
    let! prevHash = uint256Gen
    let! n = Arb.generate<uint32>
    return OutPoint(prevHash, n)
  }
  let outputGen = gen {
    let! amount = moneyGen
    let! dest = bitcoinAddressGen
    return TxOut(amount, dest :> IDestination)
  }

  let coinGen = gen {
    let! outpoint = outpointGen
    let! output = outputGen
    return Coin(outpoint, output)
  }

  let scriptDestGen(s: Script) =
    Gen.oneof [
      Gen.constant(s.Hash :> IDestination)
      Gen.constant(s.WitHash :> IDestination)
      Gen.constant(s.WitHash.ScriptPubKey.Hash :> IDestination)
    ]
  let scriptOutputGen (s: Script) = gen {
    let! amount = moneyGen
    let! dest =  scriptDestGen s
    return TxOut(amount, dest)
  }

  let scriptCoinGen = gen {
    let! s = pushScriptGen
    let! outpoint = outpointGen
    let! output = scriptOutputGen s
    let coin = Coin(outpoint, output)
    return ScriptCoin(coin, s)
  }
  let coinsGen = gen {
    let! coins =
      seq [
        coinGen |> Gen.map(fun x -> x :> ICoin)
        scriptCoinGen |> Gen.map(fun x -> x :> ICoin)
      ]
      |> Gen.sequence
    return coins
  }

  let feeRatesGen =
    moneyGen |> Gen.map(FeeRate)

type PrimitiveGenerator() =
  static member BitcoinWitScriptAddressGen(): Arbitrary<BitcoinWitScriptAddress> =
    bitcoinWitScriptAddressGen |> Arb.fromGen

  static member BitcoinWitPubKeyAddressGen() : Arbitrary<BitcoinWitPubKeyAddress> =
    bitcoinWitPubKeyAddressGen |> Arb.fromGen

  static member BitcoinPubKeyGen(): Arbitrary<PubKey> =
    gen { return (new Key()).PubKey }
    |> Arb.fromGen

  static member NodeIdGen() : Arbitrary<NodeId> =
    nodeIdGen |> Arb.fromGen

  static member NetworkSetGen() : Arbitrary<INetworkSet> =
     networkSetGen |> Arb.fromGen

  static member NetworkSetSeqGen() : Arbitrary<INetworkSet seq> =
    networkSetGen |> Gen.listOf |> Gen.map(List.toSeq) |> Arb.fromGen

  static member BitcoinAddressGen() : Arbitrary<BitcoinAddress> =
     bitcoinAddressGen |> Arb.fromGen

  static member ShortChannelIdGen() : Arbitrary<ShortChannelId> =
    shortChannelIdGen |> Arb.fromGen
  static member ShortChannelIdsGen() : Arbitrary<ShortChannelId seq> =
    shortChannelIdGen |> Gen.listOf |> Gen.map(Seq.ofList) |> Arb.fromGen

  static member MoneyGen() : Arbitrary<Money> =
    Arb.generate<int64> |> Gen.map(Money.Satoshis) |> Arb.fromGen

  static member UInt256Gen() : Arbitrary<uint256> =
    uint256Gen |> Arb.fromGen

  static member KeyGen() : Arbitrary<Key> =
    bytesOfNGen(32) |> Gen.map(fun x -> new Key(x)) |> Arb.fromGen
  static member ScriptGen(): Arbitrary<Script> =
    pushScriptGen |> Arb.fromGen

  static member String() =
    Arb.generate<char> // It seems that `Arb.generate<string>` has a bug which causes stack overflow.
    |> Gen.arrayOf
    |> Gen.filter(Seq.exists(Char.IsControl) >> not)
    |> Gen.map(String)
    |> Arb.fromGen

  static member PaymentRequest(): Arbitrary<PaymentRequest> =
    let taggedFieldGen =
      seq [
        PrimitiveGenerator.String().Generator |> Gen.map(TaggedField.DescriptionTaggedField)
        Arb.generate<DateTimeOffset>
          |> Gen.filter(fun d -> d.IsValidUnixTime())
          |> Gen.map(TaggedField.ExpiryTaggedField)
      ]
      |> Gen.oneof
    let taggedFieldsGen = gen {
      let! f = taggedFieldGen |> Gen.listOf
      let f =
        if f |> List.exists(function | TaggedField.PaymentHashTaggedField _  -> true | _ -> false) then
          f
        else
          PaymentHashTaggedField(PaymentHash (RandomUtils.GetUInt256())) :: f
      return { TaggedFields.Fields = f }
    }
    gen {
      let! m = moneyGen |> Gen.map(fun m -> m.ToLNMoney()) |> Gen.optionOf
      let! t = Arb.generate<DateTimeOffset> |> Gen.filter(fun d -> d.IsValidUnixTime())
      let! nodeSecret = keyGen
      let nodeId = nodeSecret.PubKey |> NodeId
      let! tags = taggedFieldsGen
      let r = PaymentRequest.TryCreate("lnbc", m, t, nodeId, tags, nodeSecret)
      return r
    }
    |> Gen.filter(ResultUtils.Result.isOk)
    |> Gen.map(ResultUtils.Result.deref)
    |> Arb.fromGen

  static member OutPoint() : Arbitrary<OutPoint> =
    outpointGen |> Arb.fromGen
  static member Coins() : Arbitrary<ICoin list> =
    coinsGen |> Arb.fromGen

  static member PaymentPreimage : Arbitrary<PaymentPreimage> =
    bytesOfNGen 32
    |> Gen.map(PaymentPreimage.Create)
    |> Arb.fromGen

type DomainTypeGenerator =
  static member UnixDateTime(): Arbitrary<UnixDateTime> =
      Arb.generate<uint64>
      |> Gen.map UnixDateTime.Create
      |> Gen.filter(function | Ok _ -> true | Error _ -> false)
      |> Gen.map(function Ok s -> s  | Error _ -> failwith "Unreachable")
      |> Arb.fromGen

type ResponseGenerator =
  static member LoopOut() :Arbitrary<LoopOutResponse> =
    gen {
      let! id = Arb.generate<NonNull<string>>
      let! addr = bitcoinAddressGen
      let! txid = uint256Gen |> Gen.optionOf
      return {
        LoopOutResponse.Id = id.Get
        Address = addr.ToString()
        ClaimTxId = txid }
    }
    |> Arb.fromGen

  static member LoopIn(): Arbitrary<LoopInResponse> =
    gen {
      let! id = Arb.generate<NonNull<string>>
      let! addr = bitcoinAddressGen
      return {
        LoopInResponse.Id = id.Get
        Address = addr.ToString() }
    }
    |> Arb.fromGen

  static member GetInfo(): Arbitrary<GetInfoResponse> =
    gen {
      let! v = Arb.generate<NonNull<string>>
      let! onChain = Arb.generate<SupportedCryptoCode> |> Gen.arrayOf
      let! offChain = Arb.generate<SupportedCryptoCode> |> Gen.arrayOf
      return {
        GetInfoResponse.Version = v.Get
        SupportedCoins = { SupportedCoins.OffChain = offChain; OnChain = onChain } }
    }
    |> Arb.fromGen

  static member FeeRate() : Arbitrary<FeeRate> =
    feeRatesGen |> Arb.fromGen

