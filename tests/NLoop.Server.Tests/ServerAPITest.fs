module ServerAPITest

open System.Net.Http
open FSharp.Control.Tasks

open Microsoft.AspNetCore.TestHost
open Xunit

open NLoop.Domain
open NLoopClient

[<Fact>]
[<Trait("Docker", "Off")>]
let ``ServerTest(getversion)`` () = task {
  use server = new TestServer(Helpers.getTestHost())
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

[<Fact>]
[<Trait("Docker", "Off")>]
let ``ServerTest(listen to event)`` () = task {
  ()
}
