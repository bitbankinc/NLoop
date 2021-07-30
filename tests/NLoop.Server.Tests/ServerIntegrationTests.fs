module ServerIntegrationTests

open NLoop.Domain
open Xunit.Abstractions

let pairId = struct (SupportedCryptoCode.BTC, SupportedCryptoCode.LTC)

type ServerIntegrationTestsClass(_output: ITestOutputHelper) =
  do ()
