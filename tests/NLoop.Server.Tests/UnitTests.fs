module BoltzTests

open System.IO
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Threading
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
open FSharp.Control.Tasks

[<Fact>]
[<Trait("Docker", "Off")>]
let ``BoltzClient tests (GetVersion)`` () = task {
    let b = BoltzClient("https://testnet.boltz.exchange/api/", Network.TestNet)
    let! v = b.GetVersionAsync()
    Assert.NotNull(v.Version)
  }

[<Fact>]
[<Trait("Docker", "Off")>]
let ``BoltzClient tests (GetPairs)`` () = task {
    let b = BoltzClient("https://testnet.boltz.exchange/api/", Network.TestNet)
    let! p = b.GetPairsAsync()
    Assert.NotEmpty(p.Pairs)
  }

[<Fact>]
[<Trait("Docker", "Off")>]
let ``BoltzClient tests (GetNodes)`` () = task {
    let b = BoltzClient("https://testnet.boltz.exchange/api/", Network.TestNet)
    let! p = b.GetNodesAsync()
    Assert.NotEmpty(p.Nodes)
  }

type RepositoryTests() =
  do
    Arb.register<PrimitiveGenerator>() |> ignore
  let getRepositoryProvider(caller) = task {
    let opts = NLoopOptions()
    let _ =
      let cOpts =  ChainOptions()
      cOpts.CryptoCode <- SupportedCryptoCode.BTC
      opts.ChainOptions.Add(SupportedCryptoCode.BTC, cOpts)
    let _ =
      let cOpts =  ChainOptions()
      cOpts.CryptoCode <- SupportedCryptoCode.LTC
      opts.ChainOptions.Add(SupportedCryptoCode.LTC, cOpts)
    opts.DataDir <- Path.Join(Directory.GetCurrentDirectory(), caller)
    opts.Network <- nameof(Network.RegTest)
    if (opts.DataDir |> Directory.Exists) then Directory.Delete(opts.DataDir, true)
    let repositoryProvider = RepositoryProvider(Microsoft.Extensions.Options.Options.Create(opts))
    do! (repositoryProvider :> IHostedService).StartAsync(CancellationToken.None)
    let! _ = repositoryProvider.StartCompletion
    return repositoryProvider
  }

  [<Fact>]
  [<Trait("Docker", "Off")>]
  member this.``Key and Preimage`` () = task {
    let testRepo (repo: IRepository) = unitTask {
      let key = new Key()
      do! repo.SetPrivateKey(key)
      let! k = repo.GetPrivateKey(key.PubKey.Hash)
      Assert.Equal(key.ToHex(), k.Value.ToHex())

      let preimage = RandomUtils.GetBytes(32)
      do! repo.SetPreimage(preimage)
      let! p = repo.GetPreimage(preimage |> Crypto.Hashes.Hash160)
      Assert.True(Utils.ArrayEqual(preimage, p.Value))

      ()
    }

    let! repositoryProvider = getRepositoryProvider(nameof(this.``Key and Preimage``))
    do!
      repositoryProvider.GetRepository("BTC")
      |> testRepo
    do!
      repositoryProvider.GetRepository("LTC")
      |> testRepo
    ()
  }

  [<Property(MaxTest = 20)>]
  [<Trait("Docker", "Off")>]
  member this.``Repository(LoopOut)`` (loopOut: LoopOut) =
    let testRepo (v: LoopOut) (repo: IRepository) = unitTask {
      do! repo.SetLoopOut(v)
      let! actual = repo.GetLoopOut(v.Id)
      Assert.NotEqual(actual, None)
      Assert.Equal(v, actual.Value)
    }

    let t = (task {
      let! p = getRepositoryProvider(nameof(this.``Repository(LoopOut)``))
      do!
        p.GetRepository("BTC")
        |> testRepo loopOut
      do!
        p.GetRepository("LTC")
        |> testRepo loopOut
      ()
    })
    t.GetAwaiter().GetResult()

  [<Property(MaxTest = 20)>]
  [<Trait("Docker", "Off")>]
  member this.``Repository(LoopIn)`` (loopIn: LoopIn) =
    let testRepo (v: LoopIn) (repo: IRepository) = unitTask {
      do! repo.SetLoopIn(v)
      let! actual = repo.GetLoopIn(v.Id)
      Assert.NotEqual(actual, None)
      Assert.Equal(v, actual.Value)
    }
    let t = (task {
      let! p = getRepositoryProvider(nameof(this.``Repository(LoopIn)``))
      do!
        p.GetRepository("BTC")
        |> testRepo loopIn
      do!
        p.GetRepository("LTC")
        |> testRepo loopIn
    })
    t.GetAwaiter().GetResult()
