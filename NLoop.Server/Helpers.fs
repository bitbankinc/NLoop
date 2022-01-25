namespace NLoop.Server

open NBitcoin
open NLoop.Domain
open NLoop.Domain.Utils

type [<Measure>] percent
type [<Measure>] ppm

type ComparableOutpoint = uint256 * uint

type RecordedEventPub<'T> = {
  RecordedEvent: RecordedEvent<'T>
  IsCatchUp: bool
}

exception NLoopConfigException of msg: string

[<AutoOpen>]
module internal Helpers =
  let getChainOptionString (chain: SupportedCryptoCode) (optionSubSectionName: string) =
    $"--{chain.ToString().ToLowerInvariant()}.{optionSubSectionName.ToLowerInvariant()}"

  [<Literal>]
  let private FeeBase = 1_000_000L

  let ppmToSat (amount: Money, ppm: int64<ppm>): Money =
    Money.Satoshis(amount.Satoshi * (ppm |> int64) / FeeBase)



