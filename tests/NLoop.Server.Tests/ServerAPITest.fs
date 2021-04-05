module ServerAPITest

open System
open System.Collections.Concurrent
open System.CommandLine.Binding
open System.CommandLine.Builder
open System.CommandLine.Parsing
open System.IO
open System.Net.Http

open System.Threading.Tasks
open Microsoft.AspNetCore.TestHost
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open NBitcoin.Crypto
open NLoopClient
open Xunit
open FSharp.Control.Tasks

open NLoop.CLI
open NLoop.Server

(*
let getTestRepository() =
  let keyDict = ConcurrentDictionary<_,_>()
  let preimageDict = ConcurrentDictionary<_,_>()
  { new IRepository with
      member this.SetPrivateKey(k) =
        keyDict.TryAdd(k.PubKey.Hash, k) |> ignore
        Task.FromResult() :> Task
      member this.GetPrivateKey(keyId) =
        match keyDict.TryGetValue(keyId) with
        | true, key -> Ok(key)
        | false, _ -> Error("key not found")
        |> Task.FromResult
      member this.SetPreimage(p) =
        preimageDict.TryAdd(p |> Hashes.Hash160, p) |> ignore
        Task.FromResult() :> Task
      member this.GetPreimage(hash) =
        match preimageDict.TryGetValue(hash) with
        | true, key -> Ok(key)
        | false, _ -> Error("key not found")
        |> Task.FromResult
  }
*)

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
