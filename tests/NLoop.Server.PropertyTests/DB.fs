module DB

open System.IO
open System.Threading
open Expecto
open FSharp.Control.Tasks.Affine
open Generators
open Microsoft.Extensions.Hosting
open NBitcoin
open NLoop.Server

let getRepositoryProvider () = task {
  let opts = NLoopOptions()
  let _ =
    let cOpts =  ChainOptions()
    cOpts.CryptoCode <- SupportedCryptoCode.BTC
    opts.ChainOptions.Add(SupportedCryptoCode.BTC, cOpts)
  let _ =
    let cOpts =  ChainOptions()
    cOpts.CryptoCode <- SupportedCryptoCode.LTC
    opts.ChainOptions.Add(SupportedCryptoCode.LTC, cOpts)
  opts.DataDir <- Directory.GetCurrentDirectory()
  let repositoryProvider = RepositoryProvider(Microsoft.Extensions.Options.Options.Create(opts))
  do! (repositoryProvider :> IHostedService).StartAsync(CancellationToken.None)
  let! _ = repositoryProvider.StartCompletion
  return repositoryProvider
}

let propConfig = {
  FsCheckConfig.defaultConfig with
    arbitrary = [ typeof<PrimitiveGenerator> ]
    maxTest = 10
}

[<Tests>]
let tests = testList "Repository Property Test" [
  testPropertyWithConfig propConfig "key" <| fun (key: Key) -> task {
      let testRepo key (repo: IRepository) = unitTask {
        do! repo.SetPrivateKey(key)
        let! k = repo.GetPrivateKey(key.PubKey.Hash)
        Expect.equal(key.ToHex()) (k.Value.ToHex()) ""
        ()
      }
      let! repositoryProvider = getRepositoryProvider()
      do!
        repositoryProvider.GetRepository("BTC")
        |> testRepo key
      do!
        repositoryProvider.GetRepository("LTC")
        |> testRepo key
      ()
    }

]
