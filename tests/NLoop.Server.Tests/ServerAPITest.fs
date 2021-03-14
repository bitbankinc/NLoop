module ServerAPITest

open System.IO
open System.Net
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
  ()
}

[<Fact>]
let ``ServerTest(createreverseswap)`` () = task {
  use server = new TestServer(getTestHost())
  use client = server.CreateClient()
  let! resp =
    let req = new HttpRequestMessage(HttpMethod.Post, "/v1/loop/out")
    req.Content <-
      let content = JsonSerializer.Serialize({||})
      new StringContent(content, Encoding.UTF8, "application/json")
    req
    |> client.SendAsync
  return ()
}
