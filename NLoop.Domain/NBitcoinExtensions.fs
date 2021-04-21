namespace NLoop.Domain

open System
open System.Runtime.CompilerServices
open NBitcoin
open NBitcoin.Altcoins


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
