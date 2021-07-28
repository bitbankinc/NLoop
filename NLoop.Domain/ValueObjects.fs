namespace NLoop.Domain

open System
open System.Net
open System.Runtime.CompilerServices
open System.Text.Json.Serialization
open NBitcoin
open NBitcoin.Altcoins

type PeerConnectionString = {
  NodeId: PubKey
  EndPoint: EndPoint
}
  with
  override this.ToString() =
    $"{this.NodeId.ToHex()}@{this.EndPoint.ToEndpointString()}"

  static member TryParse(str: string) =
    if (str |> isNull) then raise <| ArgumentNullException(nameof(str)) else
    let s = str.Split("@")
    if (s.Length <> 2) then Error("No @ symbol") else
    let nodeId = PubKey(s.[0])
    let addrAndPort = s.[1].Split(":")
    if (addrAndPort.Length <> 2) then Error("no : symbol in between address and port") else

    match Int32.TryParse(addrAndPort.[1]) with
    | false, _ -> Error($"Failed to parse {addrAndPort.[1]} as port")
    | true, port ->

    let endPoint =
      match IPAddress.TryParse(addrAndPort.[0]) with
      | true, ipAddr -> IPEndPoint(ipAddr, port) :> EndPoint
      | false, _ -> DnsEndPoint(addrAndPort.[0], port) :> EndPoint

    {
      NodeId = nodeId
      EndPoint = endPoint
    } |> Ok

  static member Parse(str: string) =
    match PeerConnectionString.TryParse str with
    | Ok r -> r
    | Error e -> raise <| FormatException($"Invalid connection string ({str}). {e}")

[<JsonConverter(typeof<JsonStringEnumConverter>)>]
type SupportedCryptoCode =
  | BTC = 0uy
  | LTC = 1uy

[<AbstractClass;Sealed;Extension>]
type Ext() =
  [<Extension>]
  static member ToNetworkSet(this: SupportedCryptoCode) =
    match this with
    | SupportedCryptoCode.BTC -> Bitcoin.Instance :> INetworkSet
    | SupportedCryptoCode.LTC -> Litecoin.Instance :> INetworkSet
    | _ -> failwith $"Unreachable! {this}"

[<RequireQualifiedAccess>]
module SupportedCryptoCode =
  let TryParse (s: string) =
    match s.ToUpperInvariant() with
    | "BTC" -> SupportedCryptoCode.BTC |> Some
    | "LTC" -> SupportedCryptoCode.LTC |> Some
    | _ -> None

type PairId = (struct (SupportedCryptoCode * SupportedCryptoCode))

[<RequireQualifiedAccess>]
module PairId =
  let Default = struct (SupportedCryptoCode.BTC, SupportedCryptoCode.BTC)

type SwapStatusType =
  | SwapCreated = 0uy
  | SwapExpired = 1uy

  | InvoiceSet = 10uy
  | InvoicePayed = 11uy
  | InvoicePending = 12uy
  | InvoiceSettled = 13uy
  | InvoiceFailedToPay = 14uy

  | ChannelCreated = 20uy

  | TxFailed = 30uy
  | TxMempool = 31uy
  | TxClaimed = 32uy
  | TxRefunded = 33uy
  | TxConfirmed = 34uy

  | Unknown = 255uy

[<RequireQualifiedAccess>]
module SwapStatusType =
  let FromString(s) =
    match s with
      | "swap.created" -> SwapStatusType.SwapCreated
      | "swap.expired" -> SwapStatusType.SwapExpired

      | "invoice.set" -> SwapStatusType.InvoiceSet
      | "invoice.payed" -> SwapStatusType.InvoicePayed
      | "invoice.pending" -> SwapStatusType.InvoicePending
      | "invoice.settled" -> SwapStatusType.InvoiceSettled
      | "invoice.failedToPay" -> SwapStatusType.InvoiceFailedToPay

      | "channel.created" -> SwapStatusType.ChannelCreated

      | "transaction.failed" -> SwapStatusType.TxFailed
      | "transaction.mempool" -> SwapStatusType.TxMempool
      | "transaction.claimed" -> SwapStatusType.TxClaimed
      | "transaction.refunded" -> SwapStatusType.TxRefunded
      | "transaction.confirmed" -> SwapStatusType.TxConfirmed
      | _ -> SwapStatusType.Unknown

[<AbstractClass;Sealed;Extension>]
type SwapStatusTypeExt() =

  [<Extension>]
  static member AsString(this: SwapStatusType) =
    match this with
    | SwapStatusType.SwapCreated ->
      "swap.created"
    | SwapStatusType.SwapExpired ->
      "swap.expired"
    | SwapStatusType.InvoiceSet ->
      "invoice.set"
    | SwapStatusType.InvoicePayed ->
      "invoice.payed"
    | SwapStatusType.InvoicePending ->
      "invoice.pending"
    | SwapStatusType.InvoiceSettled ->
      "invoice.settled"
    | SwapStatusType.InvoiceFailedToPay ->
      "invoice.failedToPay"

    | SwapStatusType.ChannelCreated ->
      "channel.created"

    | SwapStatusType.TxFailed ->
      "transaction.failed"
    | SwapStatusType.TxMempool ->
      "transaction.mempool"
    | SwapStatusType.TxClaimed ->
      "transaction.claimed"
    | SwapStatusType.TxRefunded ->
      "transaction.refunded"
    | SwapStatusType.TxConfirmed ->
      "transaction.confirmed"
    | _ -> "unknown"

type SwapId = SwapId of string
  with
  member this.Value = let (SwapId v) = this in v
  override this.ToString() =
    this.Value
