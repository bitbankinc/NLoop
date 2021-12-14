namespace NLoop.Server.Tests

open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open NLoop.Server
open NLoop.Server.Services
open Xunit

type BlockListenerTest() =
  [<Fact>]
  member this.TestBlockListener() =
    use server = new TestServer(TestHelpers.GetTestHost(fun services ->
      services
        .AddSingleton<RPCBlockchainListener>()
        |> ignore
    ))

    let listener = server.Services.GetRequiredService<RPCBlockchainListener>()
    Assert.NotNull(listener)
    ()
