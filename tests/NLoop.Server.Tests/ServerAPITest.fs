module ServerAPITest

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine.Binding
open System.CommandLine.Builder
open System.CommandLine.Parsing
open System.IO
open System.Net.Http

open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.TestHost
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open NBitcoin
open NBitcoin.Altcoins
open NBitcoin.Crypto
open NLoopClient
open Xunit
open FSharp.Control.Tasks

open NLoop.CLI
open NLoop.Server

let getTestRepository(n) =
  let keyDict = ConcurrentDictionary<_,_>()
  let preimageDict = ConcurrentDictionary<_,_>()
  let loopOutD = ConcurrentDictionary<_,_>()
  let loopInD = ConcurrentDictionary<_,_>()
  let jsonOpts =  JsonSerializerOptions()
  jsonOpts.AddNLoopJsonConverters(n)
  { new IRepository with
      member this.SetPrivateKey(k) =
        keyDict.TryAdd(k.PubKey.Hash, k) |> ignore
        Task.FromResult() :> Task
      member this.GetPrivateKey(keyId) =
        match keyDict.TryGetValue(keyId) with
        | true, key -> Some(key)
        | false, _ -> None
        |> Task.FromResult
      member this.SetPreimage(p) =
        preimageDict.TryAdd(p |> Hashes.Hash160, p) |> ignore
        Task.FromResult() :> Task
      member this.GetPreimage(hash) =
        match preimageDict.TryGetValue(hash) with
        | true, key -> Some(key)
        | false, _ -> None
        |> Task.FromResult
      member this.SetLoopOut(loopOut) =
        loopOutD.TryAdd (loopOut.Id, loopOut) |> ignore
        Task.FromResult() :> Task

      member this.GetLoopOut(id) =
        match loopOutD.TryGetValue(id) with
        | true, key -> Some(key)
        | false, _ -> None
        |> Task.FromResult

      member this.SetLoopIn(loopIn) =
        loopInD.TryAdd (loopIn.Id, loopIn) |> ignore
        Task.FromResult() :> Task

      member this.GetLoopIn(id) =
        match loopInD.TryGetValue(id) with
        | true, key -> Some(key)
        | false, _ -> None
        |> Task.FromResult
      member this.JsonOpts = jsonOpts
  }

let getTestRepositoryProvider() =
  let repos = Dictionary<SupportedCryptoCode, IRepository>()
  repos.Add(SupportedCryptoCode.BTC, getTestRepository(Bitcoin.Instance.Regtest))
  repos.Add(SupportedCryptoCode.LTC, getTestRepository(Litecoin.Instance.Regtest))
  { new IRepositoryProvider with
      member this.TryGetRepository(crypto) =
        match repos.TryGetValue(crypto) with
        | true, x -> Some x | false, _ -> None }


let getTestHost() =
  let rc = NLoopServerCommandLine.getRootCommand()
  let p =
    CommandLineBuilder(rc)
      .UseMiddleware(Main.useWebHostMiddleware)
      .Build()
  let parseResult = p.Parse("") // dummy to inject BindingContext so that NLoop can run `BindCommandLine` without throwing an exception
  WebHostBuilder()
    .UseContentRoot(Directory.GetCurrentDirectory())
    .ConfigureAppConfiguration(fun configBuilder ->
      configBuilder.AddJsonFile("appsettings.test.json") |> ignore
      )
    .UseStartup<Startup>()
    .ConfigureLogging(Main.configureLogging)
    .ConfigureTestServices(fun (services: IServiceCollection) ->
      services
        .AddSingleton<BindingContext>(BindingContext(parseResult))
        .AddSingleton<IRepositoryProvider>(getTestRepositoryProvider())
        |> ignore
    )
    .UseTestServer()

[<Fact>]
let ``ServerTest(getversion)`` () = task {
  use server = new TestServer(getTestHost())
  use httpClient = server.CreateClient()
  let! resp =
    new HttpRequestMessage(HttpMethod.Get, "/v1/version")
    |> httpClient.SendAsync

  let! str = resp.Content.ReadAsStringAsync()
  Assert.Equal(4, str.Split(".").Length)

  let cli = NLoopClient(httpClient)
  cli.BaseUrl <- "http://localhost"
  let! v = cli.VersionAsync()
  Assert.NotEmpty(v)
  Assert.Equal(v.Split(".").Length, 4)
}
