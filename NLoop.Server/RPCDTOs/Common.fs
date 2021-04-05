namespace NLoop.Server.DTOs

open System.Text.Json.Serialization
open NBitcoin
open NLoop.Server

type GetInfoResponse = {
  [<JsonPropertyName "version">]
  Version: string
  [<JsonPropertyName "supported_coins">]
  SupportedCoins: SupportedCoins
}
and SupportedCoins = {
  [<JsonPropertyName "on_chain">]
  OnChain: INetworkSet seq
  [<JsonPropertyName "off_chain">]
  OffChain: INetworkSet seq
}


