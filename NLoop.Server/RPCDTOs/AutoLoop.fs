namespace NLoop.Server.RPCDTOs

open NLoop.Domain
open System.Text.Json.Serialization
open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Domain.IO

type SetRuleRequest = {
  [<JsonPropertyName "incoming_threshold">]
  IncomingThreshold: Money

  [<JsonPropertyName "outgoing_threshold">]
  OutgoingThreshold: Money

  [<JsonPropertyName "short_channel_id">]
  ShortChannelId: ShortChannelId
}
  with
  member this.AsDomain = {
    AutoLoopRule.Channel = this.ShortChannelId
    IncomingThreshold = failwith "todo"
    OutgoingThreshold = failwith "todo"
  }

