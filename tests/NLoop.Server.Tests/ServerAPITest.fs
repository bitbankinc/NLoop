module ServerAPITest

open System.IO
open System.Linq
open System.Net.Http
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.TestHost
open Microsoft.AspNetCore.Hosting
open NBitcoin
open NLoop.CLI
open NLoop.Server
open NLoop.Server.Services
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Xunit
open FSharp.Control.Tasks

open NLoop.Infrastructure.DTOs
open System

let getTestHost() =
  WebHostBuilder()
    .UseContentRoot(Directory.GetCurrentDirectory())
    .ConfigureAppConfiguration(fun configBuilder ->
      configBuilder.AddJsonFile("appsettings.test.json") |> ignore
      )
    .UseStartup<Startup>()
    .ConfigureLogging(Main.configureLogging)
    .ConfigureTestServices(fun services ->
      // services.AddSingleton()
      ()
    )
    .UseTestServer()

let testClientConf = {
  NLoopClientConfig.Uri = Uri("http://localhost")
  AllowInsecure = true
  CertificateThumbPrint = None
}

[<Fact>]
let ``ServerTest(getversion)`` () = task {
  use server = new TestServer(getTestHost())
  use httpClient = server.CreateClient()
  let! resp =
    new HttpRequestMessage(HttpMethod.Get, "/v1/version")
    |> httpClient.SendAsync

  let! str = resp.Content.ReadAsStringAsync()
  Assert.Equal(4, str.Split(".").Length)

  let cli = NLoopClient(testClientConf, null, httpClient)
  let! v = cli.GetVersionAsync()
  Assert.NotEmpty(v)
  Assert.Equal(v.Split(".").Length, 4)
}

[<Fact>]
let ``ServerTest(createreverseswap)`` () = task {
  use server = new TestServer(getTestHost())
  use httpClient = server.CreateClient()
  let cli = NLoopClient(testClientConf, null, httpClient)
  let! resp = cli.LoopOutAsync()
  Assert.NotNull(resp)
}
