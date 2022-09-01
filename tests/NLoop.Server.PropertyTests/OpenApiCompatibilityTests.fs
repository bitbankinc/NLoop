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

let stjOptions = JsonSerializerOptions(IgnoreNullValues = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
stjOptions.AddNLoopJsonConverters()

let newtonsoftOpts =
  JsonSerializerSettings(NullValueHandling = NullValueHandling.Include, MissingMemberHandling = MissingMemberHandling.Error)
let check_server_to_nswag<'TGenerated, 'TNative> (input: 'TNative) =
  // 1. serialize with newtonsoft, deserialize with stj
  let jsonStr =
    JsonSerializer.Serialize<'TNative>(input, stjOptions)
  let value = JsonConvert.DeserializeObject<'TGenerated>(jsonStr, newtonsoftOpts)
  
  // 2. serialize with stj, deserialize with newtonsoft
  let jsonStr = JsonConvert.SerializeObject(value, typeof<'TGenerated>, newtonsoftOpts)
  JsonSerializer.Deserialize<'TNative>(jsonStr, stjOptions)
  |> ignore

let inline testProp testName = testPropertyWithConfig propConfig testName
let inline ftestProp testName = ftestPropertyWithConfig propConfig testName
let inline ptestProp testName = ptestPropertyWithConfig propConfig testName

[<Tests>]
let tests =
  testList "Compatibility (Server defined <---> NSwag generated)" [
    testProp "LoopOutRequest" <| fun (serverDto: LoopOutRequest) ->
      serverDto |> check_server_to_nswag<NLoopClient.LoopOutRequest, LoopOutRequest>

    testProp "LoopOutResponse" <| fun (serverDto: LoopOutResponse) ->
      serverDto |> check_server_to_nswag<NLoopClient.LoopOutResponse, LoopOutResponse>

    testProp "LoopInRequest" <| fun (serverDto: LoopInRequest) ->
      serverDto |> check_server_to_nswag<NLoopClient.LoopInRequest, LoopInRequest>

    testProp "LoopInResponse" <| fun (serverDto: LoopInResponse) ->
      serverDto |> check_server_to_nswag<NLoopClient.LoopInResponse, LoopInResponse>

    testProp "GetInfoResponse" <| fun (serverDto: GetInfoResponse) ->
      serverDto |> check_server_to_nswag<NLoopClient.GetInfoResponse, GetInfoResponse>

    testProp "OngoingSwap" <| fun (serverDto: GetOngoingSwapResponse) ->
      serverDto |> check_server_to_nswag<NLoopClient.GetOngoingSwapResponse, GetOngoingSwapResponse>

    testProp "SwapHistory" <| fun (serverDto: GetSwapHistoryResponse) ->
      serverDto |> check_server_to_nswag<NLoopClient.GetSwapHistoryResponse, GetSwapHistoryResponse>

    testProp "SuggestSwapsResponse" <| fun (serverDto: SuggestSwapsResponse) ->
      serverDto |> check_server_to_nswag<NLoopClient.SuggestSwapsResponse, SuggestSwapsResponse>

    testProp "LiquidityParameters" <| fun (serverDto: LiquidityParameters) ->
      serverDto |> check_server_to_nswag<NLoopClient.LiquidityParameters, LiquidityParameters>

    testProp "SetLiquidityParametersRequest" <| fun (serverDto: SetLiquidityParametersRequest) ->
      serverDto |> check_server_to_nswag<NLoopClient.SetLiquidityParametersRequest, SetLiquidityParametersRequest>

    testProp "GetCostSummaryResponse" <| fun (serverDTO: GetCostSummaryResponse[]) ->
      serverDTO |> check_server_to_nswag<NLoopClient.GetCostSummaryResponse, GetCostSummaryResponse[]>
  ]

