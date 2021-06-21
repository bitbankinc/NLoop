module BoltzTests

open System.IO
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Threading
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
    let repositoryProvider =
      let l = (new LoggerFactory()).CreateLogger<RepositoryProvider>()
      RepositoryProvider(Microsoft.Extensions.Options.Options.Create(opts), l)
    do! (repositoryProvider :> IHostedService).StartAsync(CancellationToken.None)
    let! _ = repositoryProvider.StartCompletion
    return repositoryProvider
  }

  [<Fact>]
  [<Trait("Docker", "Off")>]
  member this.``Key and Preimage`` () = task {
    let testRepo (repo: ISecretRepository) = unitTask {
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
