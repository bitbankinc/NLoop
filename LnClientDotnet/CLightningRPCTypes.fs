namespace LnClientDotnet

open System
open System.Text.Json.Serialization
open DotNetLightning.Payment
open DotNetLightning.Utils
open NBitcoin

module CLightningDTOs =
  type GetInfoAddress = {
    Type: string
    Address: string
    Port: int
  }

  type GetInfoResponse = {
    Address: GetInfoAddress[]
    Id: string
    Version: string
    BlockHeight: int
    Network: string
  }

  type ListChannelsResponseContent = {
    Source: PubKey
    Destination: PubKey
    ShortChannelId: ShortChannelId
    [<JsonPropertyName "public">]
    IsPublic: bool
    Satoshis: Money
    MessageFlags: uint8
    ChannelFlags: uint8
    Active: bool
    LastUpdate: uint // unit time
    BaseFeeMilliSatoshi: int64
    FeePerMillionth: uint32
    Delay: int
    Features: string
  }

  type ListChannelsResponse = {
    Channels: ListChannelsResponseContent[]
  }

  type GetInvoiceResponse = {
    PaymentHash: PaymentHash
    [<JsonPropertyName "msatoshi">]
    MilliSatoshi: LNMoney

    [<JsonPropertyName "msatoshi_received">]
    MilliSatoshiReceived: LNMoney

    [<JsonPropertyName "expiry_time">]
    ExpiryTime: DateTimeOffset

    [<JsonPropertyName "expires_at">]
    ExpiryAt: DateTimeOffset

    [<JsonPropertyName "bolt11">]
    BOLT11: PaymentRequest

    [<JsonPropertyName "pay_index">]
    PayIndex: int voption

    Label: string

    Status: string
  }
