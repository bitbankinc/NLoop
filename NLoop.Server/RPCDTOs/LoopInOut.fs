namespace NLoop.Server.DTOs

open System.Text.Json.Serialization
open DotNetLightning.Utils
open DotNetLightning.Utils.Primitives
open Giraffe
open Giraffe.HttpStatusCodeHandlers
open Giraffe.ModelValidation
open NBitcoin
open NLoop.Domain
open NLoop.Server

type LoopInRequest = {
  Amount: Money
  [<JsonPropertyName "channel_id">]
  ChannelId: ShortChannelId option
  Label: string option
  [<JsonPropertyName "counter_party_pair">]
  CounterPartyPair: SupportedCryptoCode option
}

type LoopOutRequest = {
  [<JsonPropertyName "channel_id">]
  ChannelId: ShortChannelId option
  /// The address which counterparty must pay.
  /// If none, the daemon should query a new one from LND.
  [<JsonPropertyName "address">]
  Address: BitcoinAddress option
  [<JsonPropertyName "counter_party_pair">]
  CounterPartyPair: SupportedCryptoCode option
  Amount: Money
  /// Confirmation target before we make an offer. zero-conf by default.
  [<JsonPropertyName "conf_target">]
  ConfTarget: int option
  Label: string option
}
  with
  member this.AcceptZeroConf =
    match this.ConfTarget with
    | None
    | Some(0) -> true
    | _ -> false

type LoopInResponse = {
  /// Unique id for the swap.
  [<JsonPropertyName "id">]
  Id: string
  /// An address to which we have paid.
  [<JsonPropertyName "address">]
  Address: BitcoinAddress
}

type LoopOutResponse = {
  /// Unique id for the swap.
  [<JsonPropertyName "id">]
  Id: string
  /// An address to which counterparty paid.
  [<JsonPropertyName "address">]
  Address: BitcoinAddress
  /// txid by which they have paid to us. It might be null when it is not 0-conf.
  [<JsonPropertyName "claim_tx_id">]
  ClaimTxId: uint256 option
}
