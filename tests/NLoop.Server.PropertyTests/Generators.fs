module Generators

open DotNetLightning.Utils.Primitives
open FsCheck
open NBitcoin
open NBitcoin.Altcoins
open NLoop.Server.DTOs


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
      Gen.constant Network.Main
      Gen.constant Network.TestNet
      Gen.constant Network.RegTest
    ]

  let bitcoinWitScriptAddressGen =
    gen {
      let! n = networkGen
      let! sc = pushScriptGen
      let addr = sc.WitHash.GetAddress(n)
      return addr :?> BitcoinWitScriptAddress
    }

  let bitcoinWitPubKeyAddressGen =
    gen {
      let! pk = pubKeyGen
      let! n = networkGen
      return pk.WitHash.GetAddress(n) :?> BitcoinWitPubKeyAddress
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

type PrimitiveGenerator() =
  static member BitcoinWitScriptAddressGen(): Arbitrary<BitcoinWitScriptAddress> =
    bitcoinWitScriptAddressGen |> Arb.fromGen

  static member BitcoinWitPubKeyAddressGen() : Arbitrary<BitcoinWitPubKeyAddress> =
    bitcoinWitPubKeyAddressGen |> Arb.fromGen

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
    bytesOfNGen(32) |> Gen.map Key |> Arb.fromGen
  static member ScriptGen(): Arbitrary<Script> =
    pushScriptGen |> Arb.fromGen

type ResponseGenerator =
  static member LoopOut() :Arbitrary<LoopOutResponse> =
    gen {
      let! id = Arb.generate<NonNull<string>>
      let! addr = bitcoinAddressGen
      let! txid = uint256Gen |> Gen.optionOf
      return {
        LoopOutResponse.Id = id.Get
        Address = addr
        ClaimTxId = txid }
    }
    |> Arb.fromGen

  static member LoopIn(): Arbitrary<LoopInResponse> =
    gen {
      let! id = Arb.generate<NonNull<string>>
      let! addr = bitcoinAddressGen
      return {
        LoopInResponse.Id = id.Get
        Address = addr }
    }
    |> Arb.fromGen

  static member GetInfo(): Arbitrary<GetInfoResponse> =
    gen {
      let! v = Arb.generate<NonNull<string>>
      let! onChain = networkSetGen |> Gen.arrayOf
      let! offChain = networkSetGen |> Gen.arrayOf
      return {
        GetInfoResponse.Version = v.Get
        SupportedCoins = { SupportedCoins.OffChain = offChain; OnChain = onChain } }
    }
    |> Arb.fromGen
