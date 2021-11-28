module BoltzTests

open System.IO
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Threading
open BoltzClient
open Microsoft.Extensions.Logging
open NLoop.Domain
open NLoop.Domain.IO
open Xunit
open FsCheck
open FsCheck.Xunit
open Generators
open Microsoft.Extensions.Hosting
open NBitcoin
open NLoop.Server
open NLoop.Server.Services
open NLoop.Server.SwapServerClient
open FSharp.Control.Tasks

[<Fact>]
[<Trait("Docker", "Off")>]
let ``BoltzClient tests (GetVersion)`` () = task {
    let b = BoltzClient("https://testnet.boltz.exchange/api/")
    let! v = b.GetVersionAsync()
    Assert.NotNull(v.Version)
  }

[<Fact>]
[<Trait("Docker", "Off")>]
let ``BoltzClient tests (GetPairs)`` () = task {
    let b = BoltzClient("https://testnet.boltz.exchange/api/")
    let! p = b.GetPairsAsync()
    Assert.NotEmpty(p.Pairs)
  }

[<Fact>]
[<Trait("Docker", "Off")>]
let ``BoltzClient tests (GetNodes)`` () = task {
    let b = BoltzClient("https://testnet.boltz.exchange/api/")
    let! p = b.GetNodesAsync()
    Assert.NotEmpty(p.Nodes)
  }
