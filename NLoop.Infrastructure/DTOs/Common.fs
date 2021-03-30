namespace NLoop.Infrastructure.DTOs

open System.Text.Json.Serialization
open NBitcoin
open NLoop.Infrastructure

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


