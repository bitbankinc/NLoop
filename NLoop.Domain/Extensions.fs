namespace NLoop.Domain

open System
open System
open System.Runtime.CompilerServices
open DotNetLightning.Utils
open NBitcoin
open NBitcoin.Altcoins

[<AbstractClass;Sealed;Extension>]
type PrimitiveExtensions() =
  [<Extension>]
  static member CopyWithLength(data: byte[], spanToWrite: Span<byte>) =
    let len = Utils.ToBytes(data.Length |> uint32, false)
    len.CopyTo(spanToWrite)
    data.CopyTo(spanToWrite.Slice(4))

  [<Extension>]
  static member BytesWithLength(data: byte[]) =
    let res = Array.zeroCreate(data.Length + 4)
    data.CopyWithLength(res.AsSpan())
    res

  [<Extension>]
  static member PopWithLen(this: byte[]) =
    let len = Utils.ToUInt32(this.[0..4], false) |> int32
    this.[4..(len + 4)], this.[(len + 4)..]

  [<Extension>]
  static member ToUserFriendlyString(this: ShortChannelId) =
    $"{this.AsString}:{this.ToUInt64()}"

open FsToolkit.ErrorHandling

[<AbstractClass;Sealed;Extension>]
type NBitcoinExtensions() =
  [<Extension>]
  static member GetNetworkSetFromCryptoCode(this: string) =
    match this.ToUpperInvariant() with
    | "BTC" -> Bitcoin.Instance :> INetworkSet |> Ok
    | "LTC" -> Litecoin.Instance :> INetworkSet |> Ok
    | x -> Error($"Unknown Cryptocode {x}")
  [<Extension>]
  static member GetNetworkSetFromCryptoCodeUnsafe(this: string) =
    match this.ToUpperInvariant() with
    | "BTC" -> Bitcoin.Instance :> INetworkSet
    | "LTC" -> Litecoin.Instance :> INetworkSet
    | x -> raise <| InvalidOperationException($"Unknown CryptoCode {x}")

  [<Extension>]
  static member IsValidUnixTime(this: DateTimeOffset): bool =
    let unixRef = DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
    let dt = this.ToUniversalTime()
    (unixRef <= dt) && ((dt - unixRef).TotalSeconds <= float UInt32.MaxValue)

  [<Extension>]
  static member ValidateOurSwapTxOut(this: Transaction, addressType: SwapAddressType, redeemScript: Script, expectedAmount: Money): Result<_, string> =
    result {
      let! expectedSpk =
        if addressType = SwapAddressType.P2WSH then
          Ok redeemScript.WitHash.ScriptPubKey
        else if addressType = SwapAddressType.P2SH_P2WSH then
          Ok redeemScript.WitHash.ScriptPubKey.Hash.ScriptPubKey
        else
          Error $"Unknown Address type {addressType}"

      let maybeTxo =
        this.Outputs
        |> Seq.tryFindIndex(fun txo -> txo.ScriptPubKey = expectedSpk)
      match maybeTxo with
      | None ->
        return! Error $"No HTLC Txo in swap tx output. tx: {this.ToHex()}. redeemScript: {redeemScript.ToHex()}. expected address type: {addressType}"
      | Some index ->
        let txo = this.Outputs.[index]
        if txo.Value <> expectedAmount then
          return! Error $"Tx Output Value is not the one expected (expected: {expectedAmount}) (actual: {txo.Value})"
        else
          return index |> uint32
    }
