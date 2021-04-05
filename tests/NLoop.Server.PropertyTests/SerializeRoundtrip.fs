module SerializeRoundtrip

open DotNetLightning.Utils.Primitives
open Expecto
open FsCheck
open Generators
open NBitcoin
open NBitcoin.DataEncoders
open NLoop.Server.DTOs
open NLoop.Server.Utils

open NLoop.Server

type DbDTOGen =
  static member String() =
    { new Arbitrary<NonNullString>() with
        override this.Generator = Arb.generate<string> |> Gen.filter(fun x -> x |> isNull |> not) |> Gen.map(NonNullString.CreateUnsafe) }

  static member LoopOut() =
    let ascii = ASCIIEncoder()
    gen {
      let! id = Arb.generate<NonNull<byte[]>> |> Gen.map(fun x -> x.Get |> ascii.EncodeData)
      let! state = Arb.generate<SwapType>
      let! error = Arb.generate<NonNull<byte[]>> |> Gen.map(fun x -> x.Get |> ascii.EncodeData)
      let! status = Arb.generate<int>
      let! acceptZeroConf = Arb.generate<bool>
      let! k = keyGen
      let! p = uint256Gen
      let! s = pushScriptGen
      let! i = Arb.generate<NonNull<byte[]>> |> Gen.map(fun x -> x.Get |> ascii.EncodeData)
      let! a = Arb.generate<NonNull<byte[]>> |> Gen.map(fun x -> x.Get |> ascii.EncodeData)
      let! onChainAmount = Arb.generate<int64>
      let! t = Arb.generate<uint32> |> Gen.map(BlockHeight)
      let! lTxId = uint256Gen
      let! cTxId = uint256Gen
      return {
        LoopOut.Id = id
        State = state
        Error = error
        Status = status
        AcceptZeroConf = acceptZeroConf
        PrivateKey = k
        Preimage = p
        RedeemScript = s
        Invoice = i
        ClaimAddress = a
        OnChainAmount = onChainAmount
        TimeoutBlockHeight = t
        LockupTransactionId = lTxId
        ClaimTransactionId = cTxId }
    }
    |> Arb.fromGen
let propConfig = {
  FsCheckConfig.defaultConfig with
    arbitrary = [ typeof<PrimitiveGenerator>; typeof<DbDTOGen> ] @ FsCheckConfig.defaultConfig.arbitrary
    maxTest = 100
}

let testProp = testPropertyWithConfig propConfig

[<Tests>]
let tests =
  testList "NLoop database DTOs serialization roundtrip" [
    testProp "LoopOut"  <| fun (dto: LoopOut) ->
      Expect.equal (dto) (LoopOut.FromBytes(dto.ToBytes())) "Must be the same"
  ]
