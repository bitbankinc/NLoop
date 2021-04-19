namespace NLoop.Server

[<AutoOpen>]
module internal Helpers =
  let getChainOptionString (chain: SupportedCryptoCode) (optionSubSectionName) =
    $"--{chain.ToString().ToLowerInvariant()}.{optionSubSectionName}"
