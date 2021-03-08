module ServerAPITest

open System.IO
open System.Net.Http
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.TestHost
open Microsoft.AspNetCore.Hosting
open NLoop.Server
open NLoop.Server.Services
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Xunit
open FSharp.Control.Tasks

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


[<Fact>]
let ``ServerTest(getversion)`` () = task {
  use server = new TestServer(getTestHost())
  use client = server.CreateClient()
  let! req =
    new HttpRequestMessage(HttpMethod.Get, "/v1/version")
    |> client.SendAsync

  let! str = req.Content.ReadAsStringAsync()
  Assert.Equal(4, str.Split(".").Length)
}
