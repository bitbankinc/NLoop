module BoltzTests

open NBitcoin
open NLoop.Server.Services
open Xunit
open FSharp.Control.Tasks

[<Fact>]
let ``BoltzClient tests (GetVersion)`` () = task {
    let b = BoltzClient("https://testnet.boltz.exchange/api/", Network.TestNet)
    let! v = b.GetVersionAsync()
    Assert.NotNull(v.Version)
  }

[<Fact>]
let ``BoltzClient tests (GetPairs)`` () = task {
    let b = BoltzClient("https://testnet.boltz.exchange/api/", Network.TestNet)
    let! p = b.GetPairsAsync()
    Assert.NotEmpty(p.Pairs)
  }

[<Fact>]
let ``BoltzClient tests (GetNodes)`` () = task {
    let b = BoltzClient("https://testnet.boltz.exchange/api/", Network.TestNet)
    let! p = b.GetNodesAsync()
    Assert.NotEmpty(p.Nodes)
  }

[<Fact>]
let ``Repository`` () = task {
  ()
}
