/// Property based test to check compatibility of NSwag generated client and the server.
/// See ref: https://stu.dev/property-based-testing-apis-with-fsharp/ for the concept.
module OpenApiCompatibilityTests

open System.Collections.Generic
open System.Text.Json.Serialization
open NBitcoin
open NLoop.Infrastructure.DTOs
open Generators
open Newtonsoft.Json
open System.Text.Json
open Expecto
open NLoop.Infrastructure

let propConfig = {
  FsCheckConfig.defaultConfig with
    arbitrary = [ typeof<PrimitiveGenerator>; typeof<ResponseGenerator> ]
    maxTest = 100
}

let checkCompatibilityWith<'T, 'TIn> (input: 'TIn) =
  let opts = JsonSerializerOptions(IgnoreNullValues = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
  let json =
    opts.AddNLoopJsonConverters(Network.RegTest.ChainName)
    JsonSerializer.Serialize<'TIn>(input, opts)
  let deserializeSettings = JsonSerializerSettings(NullValueHandling = NullValueHandling.Include, MissingMemberHandling = MissingMemberHandling.Error)
  JsonConvert.DeserializeObject<'T>(json, deserializeSettings)
  |> ignore

[<Tests>]
let tests =
  testList "Compatibility (Server defined <----> NSwag generated)" [
    testPropertyWithConfig propConfig "LoopOutRequest" <| fun (serverDto: LoopOutRequest) ->
      serverDto |> checkCompatibilityWith<NLoopClient.LoopOutRequest, LoopOutRequest>

    testPropertyWithConfig propConfig "LoopOutResponse" <| fun (serverDto: LoopOutResponse) ->
      serverDto |> checkCompatibilityWith<NLoopClient.LoopOutResponse, LoopOutResponse>

    testPropertyWithConfig propConfig "LoopInRequest" <| fun (serverDto: LoopInRequest) ->
      serverDto |> checkCompatibilityWith<NLoopClient.LoopInRequest, LoopInRequest>

    testPropertyWithConfig propConfig "LoopInResponse" <| fun (serverDto: LoopInResponse) ->
      serverDto |> checkCompatibilityWith<NLoopClient.LoopInResponse, LoopInResponse>

    testPropertyWithConfig propConfig "GetInfoResponse" <| fun (serverDto: GetInfoResponse) ->
      serverDto |> checkCompatibilityWith<NLoopClient.GetInfoResponse, GetInfoResponse>
  ]

