namespace LndClient

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Security.Cryptography.X509Certificates
open FSharp.Control
open Macaroons
open NBitcoin.DataEncoders
open System.Threading
open System.Threading.Tasks
open NBitcoin
open DotNetLightning.Utils
open DotNetLightning.Payment

[<RequireQualifiedAccess>]
type MacaroonInfo =
  | Raw of Macaroon
  | FilePath of string

[<RequireQualifiedAccess>]
type LndAuth=
  | FixedMacaroon of Macaroon
  | MacaroonFile of string
  | Null

[<AutoOpen>]
module private Helpers =
  do Environment.SetEnvironmentVariable("GRPC_SSL_CIPHER_SUITES", "HIGH+ECDSA");
  let parseUri str =
    match Uri.TryCreate(str, UriKind.Absolute) with
    | true, uri when uri.Scheme <> "http" && uri.Scheme <> "https" ->
      Error "uri should start from http:// or https://"
    | true, uri ->
      Ok (uri)
    | false, _ ->
      Error $"Failed to create Uri from {str}"

  let parseMacaroon (str: string) =
    printfn $"parseMacaroon: {str}"
    try
      str
      |> Macaroon.Deserialize
      |> Ok
    with
    | ex ->
      Error($"{ex}")

  let hex = HexEncoder()

  let getHash (cert: X509Certificate2) =
    use alg = System.Security.Cryptography.SHA256.Create()
    alg.ComputeHash(cert.RawData)

type ListChannelResponse = {
  Id: ShortChannelId
  Cap: Money
  LocalBalance: Money
  NodeId: PubKey
}

type GetChannelInfoResponse = {
  Capacity: Money
  Node1Policy: NodePolicy
  Node2Policy: NodePolicy
}
and NodePolicy = {
  Id: PubKey
  TimeLockDelta: BlockHeightOffset16
  MinHTLC: LNMoney
  FeeBase: LNMoney
  FeeProportionalMillionths: LNMoney
  Disabled: bool
}

type ChannelEventUpdate =
  | OpenChannel of ListChannelResponse
  | PendingOpenChannel of OutPoint
  | ClosedChannel of  {| Id: ShortChannelId; CloseTxHeight: BlockHeight; TxId: uint256 |}
  | ActiveChannel of OutPoint
  | InActiveChannel of OutPoint
  | FullyResolvedChannel of OutPoint

type ILightningChannelEventsListener =
  inherit IDisposable
  abstract member WaitChannelChange: unit -> Task<ChannelEventUpdate>

type LndOpenChannelRequest = {
  Private: bool option
  CloseAddress: string option
  NodeId: PubKey
  Amount: LNMoney
}

type LndOpenChannelError = {
  StatusCode: int option
  Message: string
}
type SendPaymentRequest = {
  Invoice: PaymentRequest
  MaxFee: Money
  OutgoingChannelIds: ShortChannelId[]
}

type PaymentResult = {
  PaymentPreimage: Primitives.PaymentPreimage
  Fee: LNMoney
}

type InvoiceStateEnum =
   | Open = 0
   | Settled = 1
   | Canceled = 2
   | Accepted = 3

type InvoiceSubscription = {
  PaymentRequest: PaymentRequest
  InvoiceState: InvoiceStateEnum
  AmountPayed: Money
}
type RouteHint = {
  Hops: HopHint []
}
and HopHint = {
  NodeId: NodeId
  ShortChannelId: ShortChannelId
  FeeBase: LNMoney
  FeeProportionalMillionths: uint32
  CLTVExpiryDelta: BlockHeightOffset16
}

type INLoopLightningClient =
  abstract member GetDepositAddress: ?ct: CancellationToken -> Task<BitcoinAddress>
  abstract member GetHodlInvoice:
    paymentHash: Primitives.PaymentHash *
    value: LNMoney *
    expiry: TimeSpan *
    routeHints: RouteHint[] *
    memo: string *
    ?ct: CancellationToken
      -> Task<PaymentRequest>
  abstract member GetInvoice:
    paymentPreimage: PaymentPreimage *
    amount: LNMoney *
    expiry: TimeSpan *
    routeHint: RouteHint[] *
    memo: string *
    ?ct: CancellationToken
     -> Task<PaymentRequest>
  abstract member Offer: req: SendPaymentRequest * ?ct: CancellationToken -> Task<Result<PaymentResult, string>>
  abstract member GetInfo: ?ct: CancellationToken  -> Task<obj>
  abstract member QueryRoutes: nodeId: PubKey * amount: LNMoney * ?ct: CancellationToken -> Task<Route>
  abstract member OpenChannel: request: LndOpenChannelRequest * ?ct: CancellationToken -> Task<Result<OutPoint, LndOpenChannelError>>
  abstract member ConnectPeer: nodeId: PubKey * host: string * ?ct: CancellationToken -> Task
  abstract member ListChannels: ?ct: CancellationToken -> Task<ListChannelResponse list>
  abstract member SubscribeChannelChange: ?ct: CancellationToken -> AsyncSeq<ChannelEventUpdate>
  abstract member SubscribeSingleInvoice: invoiceHash: PaymentHash * ?c: CancellationToken -> AsyncSeq<InvoiceSubscription>
  abstract member GetChannelInfo: channelId: ShortChannelId * ?ct:CancellationToken -> Task<GetChannelInfoResponse>

