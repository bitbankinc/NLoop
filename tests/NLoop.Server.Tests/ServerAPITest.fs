module ServerAPITest

open System.Net.Http
open FSharp.Control.Tasks

open LndClient
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open NLoop.Server.Tests
open Xunit

open NLoop.Domain
open NLoopClient
open NLoop.Server.DTOs

type ServerAPITest() =
  [<Fact>]
  [<Trait("Docker", "Off")>]
  member this.``ServerTest(getversion)`` () = task {
    use server = new TestServer(TestHelpers.GetTestHost())
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

  static member TestValidateLoopOutData =
    [
      ("mainnet address with mainnet backend", ())
    ]
    |> Seq.map(fun (name, _) -> [|
      name |> box
    |])

  [<Theory>]
  [<MemberData(nameof(ServerAPITest.TestValidateLoopOutData))>]
  [<Trait("Docker", "Off")>]
  member this.TestValidateConfTarget(name, channels) = task {
    use server = new TestServer(TestHelpers.GetTestHost(fun (sp: IServiceCollection) ->
        sp
          .AddSingleton<INLoopLightningClient>(TestHelpers.GetDummyLightningClient({
            DummyLnClientParameters.ListChannels = channels
          }))
          |> ignore
      ))

    let client = server.CreateClient()
    let chan1Rec =
      (*
      {
        LoopOutRequest.Amount = swapAmount
        ChannelIds = [| chanId1 |] |> ValueSome
        Address = None
        PairId = pairId |> Some
        SwapTxConfRequirement = pairId.DefaultLoopOutParameters.SwapTxConfRequirement.Value |> int |> Some
        Label = None
        MaxSwapRoutingFee = routingFee |> ValueSome
        MaxPrepayRoutingFee = prepayFee |> ValueSome
        MaxSwapFee = testQuote.SwapFee |> ValueSome
        MaxPrepayAmount = testQuote.PrepayAmount |> ValueSome
        MaxMinerFee = testQuote.SweepMinerFee |> AutoLoopHelpers.scaleMinerFee |> ValueSome
        SweepConfTarget = pairId.DefaultLoopOutParameters.SweepConfTarget.Value |> int |> ValueSome
      }
      *)
      failwith "todo"
    let! resp =
      new HttpRequestMessage(HttpMethod.Post, "/v1/loop/out")
      |> client.SendAsync
    ()
  }
