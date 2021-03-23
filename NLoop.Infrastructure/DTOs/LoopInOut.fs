namespace NLoop.Infrastructure.DTOs

open System.Text.Json.Serialization
open DotNetLightning.Utils.Primitives
open NBitcoin

type LoopInRequest = {
  Amount: Money
  External: bool
  ConfTarget: int
  LastHop: NodeId
  Label: string option
}

type LoopOutRequest = {
  [<JsonPropertyName "channel_id">]
  ChannelId: ShortChannelId option
  [<JsonPropertyName "address">]
  Address: BitcoinAddress
  [<JsonPropertyName "counter_party_pair">]
  CounterPartyPair: INetworkSet option
  Amount: Money
  /// Confirmation target before we make an offer. zero-conf by default.
  [<JsonPropertyName "conf_target">]
  ConfTarget: int option
  Label: string option
}

type LoopInResponse = {
  [<JsonPropertyName "id">]
  Id: string
}

type LoopOutResponse = {
  [<JsonPropertyName "id">]
  Id: string
  [<JsonPropertyName "htlc_target">]
  HtlcTarget: BitcoinAddress
}
