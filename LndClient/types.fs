namespace LndClient

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Security.Cryptography.X509Certificates
open DotNetLightning.ClnRpc
open FSharp.Control
open Macaroons
open NBitcoin.DataEncoders
open System.Threading
open System.Threading.Tasks
open NBitcoin
open DotNetLightning.Utils
open DotNetLightning.Payment
open NBitcoin.RPC

[<RequireQualifiedAccess>]
type MacaroonInfo =
  | Raw of Macaroon
  | FilePath of string

[<RequireQualifiedAccess>]
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
  RemoteBalance: Money
  NodeId: PubKey
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

[<Extension;AbstractClass;Sealed>]
type RouteHintExtensions =
  [<Extension>]
  static member TryGetLastHop(this: RouteHint[]) =
    let lastHops =
      this |> Array.map(fun rh -> rh.Hops.[0].NodeId)
    if lastHops |> Seq.distinct |> Seq.length = 1 then
      lastHops.[0].Value |> Some
    else
      // if we don't have unique last hop in route hints, we don't know what will be
      // the last hop. (it is up to the counterparty)
      None

type NodePolicy = {
  Id: PubKey
  TimeLockDelta: BlockHeightOffset16
  MinHTLC: LNMoney
  FeeBase: LNMoney
  FeeProportionalMillionths: uint32
  Disabled: bool
}

type GetChannelInfoResponse = {
  Capacity: Money
  Node1Policy: NodePolicy
  Node2Policy: NodePolicy
}
  with
  member this.ToRouteHints(channelId: ShortChannelId) =
    let hopHint =
      {
        HopHint.NodeId = this.Node1Policy.Id |> NodeId
        HopHint.ShortChannelId = channelId
        HopHint.FeeBase = this.Node1Policy.FeeBase
        HopHint.FeeProportionalMillionths = this.Node1Policy.FeeProportionalMillionths
        HopHint.CLTVExpiryDelta = this.Node1Policy.TimeLockDelta
      }
    { RouteHint.Hops = [|hopHint|] }


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

  /// Timeout before giving up making an offer.
  /// note: This is not a timeout for payment to complete, that is defined by invoice parameters.
  /// If that does not click to you, see: https://docs.lightning.engineering/lightning-network-tools/lnd/payments
  TimeoutSeconds: int
}

type PaymentResult = {
  PaymentPreimage: Primitives.PaymentPreimage
  Fee: LNMoney
}

type OfferResult = unit

[<Struct>]
type OutgoingInvoiceStateUnion =
   | InFlight
   | Failed
   | Succeeded
   | Unknown

type OutgoingInvoiceSubscription = {
  PaymentRequest: PaymentRequest
  InvoiceState: OutgoingInvoiceStateUnion
  Fee: LNMoney
  AmountPayed: LNMoney
}

[<Struct>]
type IncomingInvoiceStateUnion =
  | Open
  | Accepted
  | Canceled
  | Settled
  | Unknown

type IncomingInvoiceSubscription = {
  PaymentRequest: PaymentRequest
  InvoiceState: IncomingInvoiceStateUnion
  AmountPayed: LNMoney
}
type GetChannelInfo = ShortChannelId -> Task<GetChannelInfoResponse option>
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

  /// Send payment to the counterparty, expect to finish immediately.
  abstract member SendPayment: req: SendPaymentRequest * ?ct: CancellationToken -> Task<Result<PaymentResult, string>>

  /// Make an payment offer to the counterparty, do not expect immediately.
  abstract member Offer: req: SendPaymentRequest * ?ct: CancellationToken -> Task<Result<OfferResult, string>>
  abstract member GetInfo: ?ct: CancellationToken -> Task<obj>
  abstract member QueryRoutes: nodeId: PubKey * amount: LNMoney * ?maybeOutgoingChanId: ShortChannelId * ?ct: CancellationToken ->
    Task<Route>
  abstract member ConnectPeer: nodeId: PubKey * host: string * ?ct: CancellationToken -> Task
  abstract member ListChannels: ?ct: CancellationToken -> Task<ListChannelResponse list>

  /// Subscription for incoming payment. used for loopin
  abstract member SubscribeSingleInvoice: invoiceHash: PaymentHash * ?c: CancellationToken ->
    AsyncSeq<IncomingInvoiceSubscription>
  /// Subscription for outgoing payment. used for loopout
  abstract member TrackPayment: invoiceHash: PaymentHash * ?c: CancellationToken ->
    AsyncSeq<OutgoingInvoiceSubscription>
  abstract member GetChannelInfo: channelId: ShortChannelId * ?ct:CancellationToken -> Task<GetChannelInfoResponse option>

open FSharp.Control.Tasks

type WalletUtxo = {
  Address: BitcoinAddress
  Amount: Money
  PrevOut: OutPoint
  MaybeRedeem: Script option
}
  with
  member this.AsCoin() =
    let c = ScriptCoin()
    c.Amount <- this.Amount
    c.Outpoint <- this.PrevOut
    c.TxOut <-
      TxOut(this.Amount, this.Address.ScriptPubKey)
    match this.MaybeRedeem with
    | Some redeem ->
      c.ToScriptCoin(redeem) :> ICoin
    | None -> c :> ICoin

  static member FromRPCDto(u: UnspentCoin) =
    {
      WalletUtxo.Address = u.Address
      Amount = u.Amount
      PrevOut = u.OutPoint
      MaybeRedeem = u.RedeemScript |> Option.ofObj
    }


type WalletClientError =
  | InSufficientFunds of string
  | RPCError of string

type IWalletClient =
  abstract member FundToAddress: dest: BitcoinAddress * amount: Money * confTarget: BlockHeightOffset32 * ?ct: CancellationToken ->
    Task<uint256>
  abstract member GetDepositAddress: network: Network * ?ct: CancellationToken -> Task<BitcoinAddress>

  abstract member GetSendingTxFee:
    destinations: IDictionary<BitcoinAddress, Money> *
      target: BlockHeightOffset32 *
      ?ct: CancellationToken ->
    Task<Result<Money, WalletClientError>>

[<AbstractClass;Sealed;Extension>]
type LightningClientExtensions =

  [<Extension>]
  static member GetRouteHints(this: INLoopLightningClient, channelId: ShortChannelId, ?ct: CancellationToken) = task {
    let ct = defaultArg ct CancellationToken.None
    match! this.GetChannelInfo(channelId, ct) with
    | Some c ->
      return c.ToRouteHints(channelId)
    | None ->
      return failwith "todo"
  }


type NLoopLightningClientError =
  | Lnd of Grpc.Core.RpcException
  | Cln of CLightningRPCException

exception NLoopLightningClientException of NLoopLightningClientError
