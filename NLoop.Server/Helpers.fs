namespace NLoop.Server

open NBitcoin
open NLoop.Domain

type [<Measure>] percent
type [<Measure>] ppm

[<AutoOpen>]
module internal Helpers =
  let getChainOptionString (chain: SupportedCryptoCode) (optionSubSectionName: string) =
    $"--{chain.ToString().ToLowerInvariant()}.{optionSubSectionName.ToLowerInvariant()}"

  [<Literal>]
  let private FeeBase = 100000L
  let ppmToSat (amount: Money, ppm: int64<ppm>): Money =
    Money.Satoshis(amount.Satoshi * (ppm |> int64) / FeeBase)



