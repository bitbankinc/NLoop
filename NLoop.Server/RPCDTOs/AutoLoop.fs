namespace NLoop.Server.RPCDTOs

open System.Text.Json.Serialization
open DotNetLightning.Utils.Primitives
open NBitcoin

type SetRuleRequest = {
  [<JsonPropertyName "incoming_threshold">]
  IncomingThreshold: Money

  [<JsonPropertyName "outgoing_threshold">]
  OutgoingThreshold: Money

  [<JsonPropertyName "channel_pubkey">]
  ChannelPubKey: PubKey option

  [<JsonPropertyName "short_channel_id">]
  ShortChannelId: ShortChannelId option
}
