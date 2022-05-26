/// Property based test to check compatibility of NSwag generated client and the server.
/// See ref: https://stu.dev/property-based-testing-apis-with-fsharp/ for the concept.
module OpenApiCompatibilityTests

open System.Collections.Generic
open System.Text.Json.Serialization
open NBitcoin
open NLoop.Server.DTOs
open Generators
open NLoop.Server.RPCDTOs
open Newtonsoft.Json
open System.Text.Json
open Expecto
open NLoop.Server
open NLoop.Domain.IO

let propConfig = {
  FsCheckConfig.defaultConfig with
    arbitrary = [ typeof<PrimitiveGenerator>; typeof<ResponseGenerator> ]
    maxTest = 100
}

let checkCompatibilityWith<'T, 'TIn> (input: 'TIn) =
  let opts = JsonSerializerOptions(IgnoreNullValues = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
  let json =
    opts.AddNLoopJsonConverters()
    JsonSerializer.Serialize<'TIn>(input, opts)
  let deserializeSettings = JsonSerializerSettings(NullValueHandling = NullValueHandling.Include, MissingMemberHandling = MissingMemberHandling.Error)
  JsonConvert.DeserializeObject<'T>(json, deserializeSettings)
  |> ignore

let inline testProp testName = testPropertyWithConfig propConfig testName
let inline ftestProp testName = ftestPropertyWithConfig propConfig testName
let inline ptestProp testName = ptestPropertyWithConfig propConfig testName

[<Tests>]
let tests =
  testList "Compatibility (Server defined <----> NSwag generated)" [
    testProp "LoopOutRequest" <| fun (serverDto: LoopOutRequest) ->
      serverDto |> checkCompatibilityWith<NLoopClient.LoopOutRequest, LoopOutRequest>

    testProp "LoopOutResponse" <| fun (serverDto: LoopOutResponse) ->
      serverDto |> checkCompatibilityWith<NLoopClient.LoopOutResponse, LoopOutResponse>

    testProp "LoopInRequest" <| fun (serverDto: LoopInRequest) ->
      serverDto |> checkCompatibilityWith<NLoopClient.LoopInRequest, LoopInRequest>

    testProp "LoopInResponse" <| fun (serverDto: LoopInResponse) ->
      serverDto |> checkCompatibilityWith<NLoopClient.LoopInResponse, LoopInResponse>

    testProp "GetInfoResponse" <| fun (serverDto: GetInfoResponse) ->
      serverDto |> checkCompatibilityWith<NLoopClient.GetInfoResponse, GetInfoResponse>

    testProp "OngoingSwap" <| fun (serverDto: GetOngoingSwapResponse) ->
      serverDto |> checkCompatibilityWith<NLoopClient.GetOngoingSwapResponse, GetOngoingSwapResponse>

    testProp "SwapHistory" <| fun (serverDto: GetSwapHistoryResponse) ->
      serverDto |> checkCompatibilityWith<NLoopClient.GetSwapHistoryResponse, GetSwapHistoryResponse>

    testProp "SuggestSwapsResponse" <| fun (serverDto: SuggestSwapsResponse) ->
      serverDto |> checkCompatibilityWith<NLoopClient.SuggestSwapsResponse, SuggestSwapsResponse>

    testProp "LiquidityParameters" <| fun (serverDto: LiquidityParameters) ->
      serverDto |> checkCompatibilityWith<NLoopClient.LiquidityParameters, LiquidityParameters>

    testProp "SetLiquidityParametersRequest" <| fun (serverDto: SetLiquidityParametersRequest) ->
      serverDto |> checkCompatibilityWith<NLoopClient.SetLiquidityParametersRequest, SetLiquidityParametersRequest>

    testProp "GetCostSummaryResponse" <| fun (serverDTO: GetCostSummaryResponse[]) ->
      serverDTO |> checkCompatibilityWith<NLoopClient.GetCostSummaryResponse, GetCostSummaryResponse[]>
  ]

