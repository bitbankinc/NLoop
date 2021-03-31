namespace NLoop.Server

open System.Runtime.CompilerServices
open NBitcoin
open NBitcoin.Altcoins


[<AbstractClass;Sealed;Extension>]
type NBitcoinExtensions() =
  [<Extension>]
  static member GetNetworkFromCryptoCode(this: string) =
    match this.ToUpperInvariant() with
    | "BTC" -> Bitcoin.Instance :> INetworkSet |> Ok
    | "LTC" -> Litecoin.Instance :> INetworkSet |> Ok
    | x -> Error($"Unknown Cryptocode {x}")
