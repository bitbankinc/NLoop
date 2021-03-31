namespace NLoop.Server.DTOs

open System.Runtime.CompilerServices
open System.Text.Json.Serialization


[<JsonConverter(typeof<JsonStringEnumConverter>)>]
type SwapType =
  | Submarine = 0
  | ReverseSubmarine = 1

[<JsonConverter(typeof<JsonStringEnumConverter>)>]
type OrderType =
  | buy = 0
  | sell = 1

 [<AbstractClass;Sealed;Extension>]
type Extensions() =
  [<Extension>]
  static member  IsLoopIn(this: SwapType) =
    this = SwapType.ReverseSubmarine
