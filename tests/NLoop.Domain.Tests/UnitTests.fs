module UnitTests

open System
open System.Threading
open DotNetLightning.Utils.Primitives
open NBitcoin
open NBitcoin.DataEncoders
open NLoop.Domain
open Xunit
open FsToolkit.ErrorHandling

[<Fact>]
let ``SwapScriptValidationTest`` () =
  let hex = HexEncoder()
  let redeemScript =
    "a9140d90b94f98198ea9ba3a94a34d27897c27024305876321037c7980160182adad9eaea06c1b1cdf9dfdce5ef865c386a112bff4a62196caf66702f800b1752103de7f16653d93ff6ceac681050e75692d7a6fa05ea473d7df90aeac40fa11e28d68ac"
    |> Script.FromHex
  let preimageHash = uint256(hex.DecodeData "26cb777d4fa07a4fe47aa25bed4db29dfe32edfaac3f708299decc6d1199109c")
  use key =
    "88c4ac1e6d099ea63eda4a0ae4863420dbca9aa1bce536aa63d46db28c7b780e"
    |> hex.DecodeData
    |> fun h -> new Key(h)
  let timeoutBlockHeight = BlockHeight(248u)
  let t = Scripts.validateSwapScript(preimageHash) key.PubKey timeoutBlockHeight redeemScript
  Assert.Equal(t, Ok())

  let e = Scripts.validateSwapScript(uint256.Zero) (key.PubKey) timeoutBlockHeight redeemScript
  Assert.True(e |> Result.isError)
  let e = Scripts.validateSwapScript(preimageHash) ((new Key()).PubKey) timeoutBlockHeight redeemScript
  Assert.True(e |> Result.isError)
  let e = Scripts.validateSwapScript(preimageHash) key.PubKey BlockHeight.Zero redeemScript
  Assert.True(e |> Result.isError)
  ()
[<Fact>]
let ``ReverseSwapScriptValidationTest`` () =
  let hex = HexEncoder()
  let redeemScript =
    "8201208763a9147ba0ab22fcffda41fd324aba4b5ce192ba9ec5dd882102e82694032768e49526972307874d868b67c87c37e9256c05a2c5c0474e7395e3677502f800b175210247d7443123302272524c9754b44a6e7e6e1236719e9f468e15927aa4ea26301168ac"
    |> Script.FromHex
  let preimageHash =
    "fa9ef1d253d34e9e44da97b00c6ec6a95058f646de35ddb7649fc3313ac6fc61"
    |> hex.DecodeData
    |> uint256

  use claimKey =
    "dddc90e33843662631fb8c3833c4743ffd8f00a94715735633bf178e62eb291c"
    |> hex.DecodeData
    |> fun x -> new Key(x)

  let timeoutBlockHeight = BlockHeight(248u)
  let t = Scripts.validateReverseSwapScript preimageHash claimKey.PubKey timeoutBlockHeight redeemScript
  Assert.Equal(t, Ok())

  let e = Scripts.validateReverseSwapScript uint256.Zero claimKey.PubKey timeoutBlockHeight redeemScript
  Assert.True(e |> Result.isError)
  let e = Scripts.validateReverseSwapScript preimageHash ((new Key()).PubKey) timeoutBlockHeight redeemScript
  Assert.True(e |> Result.isError)
  let e = Scripts.validateReverseSwapScript preimageHash claimKey.PubKey BlockHeight.Zero redeemScript
  Assert.True(e |> Result.isError)
  ()
