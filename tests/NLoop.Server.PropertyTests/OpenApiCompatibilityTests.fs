/// Property based test to check compatibility of NSwag generated client and the server.
/// See ref: https://stu.dev/property-based-testing-apis-with-fsharp/ for the concept.
module OpenApiCompatibilityTests

open NBitcoin
open NLoop.Infrastructure.DTOs
open Generators
open Newtonsoft.Json
open System.Text.Json
open Expecto
open NLoop.Infrastructure

let propConfig = {
  FsCheckConfig.defaultConfig with
    arbitrary = [ typeof<PrimitiveGenerator> ]
    maxTest = 100
}

let inline checkCompatibilityWith<'T, 'TIn> (input: 'TIn) =
  let opts = JsonSerializerOptions(IgnoreNullValues = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
  let json =
    opts.AddNLoopJsonConverters(Network.RegTest.ChainName)
    JsonSerializer.Serialize<'TIn>(input, opts)
  let deserializeSettings = JsonSerializerSettings(NullValueHandling = NullValueHandling.Include, MissingMemberHandling = MissingMemberHandling.Error)
  JsonConvert.DeserializeObject<'T>(json, deserializeSettings) |> ignore

[<Tests>]
let tests =
  testList "Compatibility (Server defined <----> NSwag generated)" [
    testPropertyWithConfig propConfig "LoopOutResponse" <| fun (serverDto: LoopOutResponse) ->
      serverDto |> checkCompatibilityWith<NLoopClient.LoopOutResponse, LoopOutResponse>

    testPropertyWithConfig propConfig "LoopOutRequest" <| fun (serverDto: LoopOutRequest) ->
      serverDto |> checkCompatibilityWith<NLoopClient.LoopOutRequest, LoopOutRequest>
  ]
