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
  Channel: ShortChannelId
  Address: BitcoinAddress option
  CounterPartyPair: INetworkSet option
  Amount: Money
  External: bool
  ConfTarget: int option
  LastHop: NodeId
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
