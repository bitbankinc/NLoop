namespace NLoop.Domain

open System
open System
open System.Runtime.CompilerServices
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
