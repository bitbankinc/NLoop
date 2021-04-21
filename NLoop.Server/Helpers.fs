namespace NLoop.Server

open NLoop.Domain

[<AutoOpen>]
module internal Helpers =
  let getChainOptionString (chain: SupportedCryptoCode) (optionSubSectionName) =
    $"--{chain.ToString().ToLowerInvariant()}.{optionSubSectionName}"
