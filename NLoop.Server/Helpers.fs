namespace NLoop.Server

open NBitcoin
open NLoop.Domain

[<AutoOpen>]
module internal Helpers =
  let getChainOptionString (chain: SupportedCryptoCode) (optionSubSectionName: string) =
    $"--{chain.ToString().ToLowerInvariant()}.{optionSubSectionName.ToLowerInvariant()}"

type [<Measure>] percent
type [<Measure>] ppm

[<RequireQualifiedAccess>]
module ValueOption =
  let defaultToVeryHighFee(v: Money voption) =
    v |> ValueOption.defaultValue(Money.Coins(100000m))

