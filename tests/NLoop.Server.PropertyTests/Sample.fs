module Tests

open NLoop.Infrastructure.DTOs
open Newtonsoft.Json
open System.Text.Json
open System.Text.Json.Serialization
open Expecto

let checkCompatibilityWith<'T> input =
  let opts = JsonSerializerOptions(IgnoreNullValues = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
  opts.Converters.Add(JsonFSharpConverter(JsonUnionEncoding.FSharpLuLike))

  let json = JsonSerializer.Serialize(input, opts)
  let deserializerSettings = JsonSerializerSettings(NullValueHandling = NullValueHandling.Include, MissingMemberHandling = MissingMemberHandling.Error)
  JsonConvert.DeserializeObject<'T>(json, deserializerSettings) |> ignore

[<Tests>]
let tests =
  testList "samples" [
    testProperty "Loop out" <| fun (req: LoopOutResponse) ->
      ()
  ]
