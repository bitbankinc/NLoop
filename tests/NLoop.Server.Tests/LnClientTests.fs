namespace NLoop.Server.Tests

open System.IO
open LnClientDotnet
open Xunit

module CLightningClientTests =
  [<Fact>]
  let canRequestToClightning() =
    ()

module PluginTests =
  [<Fact>]
  let ``It can run `` =
    use inMem = new MemoryStream()
    use inStream = new StreamReader(inMem)
    use outMem = new MemoryStream()
    let outStream = new StreamWriter(outMem)
    let summaryPlugin =
      Plugin.empty
      |> Plugin.setMockStdIn(inStream)
      |> Plugin.setMockStdOut(outStream)
    ()
