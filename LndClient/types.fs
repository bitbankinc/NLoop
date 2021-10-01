namespace LndClient

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
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
    try
      str
      |> Macaroon.Deserialize
      |> Ok
    with
    | ex ->
      Error($"{ex}")

  let hex = HexEncoder()
[<Extension;AbstractClass;Sealed>]
type GrpcTypeExt =
  [<Extension>]
  static member ToOutPoint(a: Lnrpc.ChannelPoint) =
    let o = OutPoint()
    o.Hash <-
      if a.FundingTxidCase = Lnrpc.ChannelPoint.FundingTxidOneofCase.FundingTxidBytes then
        a.FundingTxidBytes.ToByteArray() |> uint256
      elif a.FundingTxidCase = Lnrpc.ChannelPoint.FundingTxidOneofCase.FundingTxidStr then
        a.FundingTxidStr |> uint256.Parse
      else
        assert(a.FundingTxidCase = Lnrpc.ChannelPoint.FundingTxidOneofCase.None)
        null
    o.N <- a.OutputIndex
    o
  [<Extension>]
  static member ToOutPoint(a: Lnrpc.PendingUpdate) =
    let o = OutPoint()
    o.Hash <-
      a.Txid.ToByteArray() |> uint256
    o.N <-
      a.OutputIndex
    o

type ListChannelResponse = {
  Id: ShortChannelId
  Cap: Money
  LocalBalance: Money
  NodeId: PubKey
}
  with
  static member FromGrpcType(o: Lnrpc.Channel) =
      {
        ListChannelResponse.Id = o.ChanId |> ShortChannelId.FromUInt64
        Cap = o.Capacity |> Money.Satoshis
        LocalBalance = o.LocalBalance |> Money.Satoshis
        NodeId = o.RemotePubkey |> PubKey }
type ChannelEventUpdate =
  | OpenChannel of ListChannelResponse
  | PendingOpenChannel of OutPoint
  | ClosedChannel of  {| Id: ShortChannelId; CloseTxHeight: BlockHeight; TxId: uint256 |}
  | ActiveChannel of OutPoint
  | InActiveChannel of OutPoint
  | FullyResolvedChannel of OutPoint
  static member FromGrpcType(r: Lnrpc.ChannelEventUpdate) =
    match r.Type with
    | Lnrpc.ChannelEventUpdate.Types.UpdateType.ActiveChannel ->
      r.ActiveChannel.ToOutPoint()
      |> ChannelEventUpdate.ActiveChannel
    | Lnrpc.ChannelEventUpdate.Types.UpdateType.InactiveChannel ->
      r.InactiveChannel.ToOutPoint()
      |> ChannelEventUpdate.InActiveChannel
    | Lnrpc.ChannelEventUpdate.Types.UpdateType.OpenChannel ->
      r.OpenChannel
      |> ListChannelResponse.FromGrpcType
      |> OpenChannel
    | Lnrpc.ChannelEventUpdate.Types.UpdateType.PendingOpenChannel ->
      r.PendingOpenChannel.ToOutPoint()
      |> PendingOpenChannel
    | Lnrpc.ChannelEventUpdate.Types.UpdateType.ClosedChannel ->
      let c = r.ClosedChannel
      {|
        Id = c.ChanId |> ShortChannelId.FromUInt64
        CloseTxHeight = c.CloseHeight |> BlockHeight
        TxId = c.ClosingTxHash |> hex.DecodeData |> uint256
      |}
      |> ClosedChannel
    | Lnrpc.ChannelEventUpdate.Types.UpdateType.FullyResolvedChannel ->
      r.FullyResolvedChannel.ToOutPoint()
      |> FullyResolvedChannel
    | x -> failwith $"Unreachable! Unknown type {x}"

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

[<RequireQualifiedAccess>]
type FeeLimit =
  | Fixed of Money
  | Percent of int
type SendPaymentRequest = {
  Invoice: PaymentRequest
  MaxFee: FeeLimit
  OutgoingChannelId: ShortChannelId option
}

type INLoopLightningClient =
  abstract member GetDepositAddress: ?ct: CancellationToken -> Task<BitcoinAddress>
  abstract member GetHodlInvoice:
    paymentHash: Primitives.PaymentHash *
    value: LNMoney *
    expiry: TimeSpan *
    memo: string *
    ?ct: CancellationToken
      -> Task<PaymentRequest>
  abstract member GetInvoice:
    paymentPreimage: PaymentPreimage *
    amount: LNMoney *
    expiry: TimeSpan *
    memo: string *
    ?ct: CancellationToken
     -> Task<PaymentRequest>
  abstract member Offer: req: SendPaymentRequest * ?ct: CancellationToken -> Task<Result<Primitives.PaymentPreimage, string>>
  abstract member GetInfo: ?ct: CancellationToken  -> Task<obj>
  abstract member QueryRoutes: nodeId: PubKey * amount: LNMoney * ?ct: CancellationToken -> Task<Route>
  abstract member OpenChannel: request: LndOpenChannelRequest * ?ct: CancellationToken -> Task<Result<OutPoint, LndOpenChannelError>>
  abstract member ConnectPeer: nodeId: PubKey * host: string * ?ct: CancellationToken -> Task
  abstract member ListChannels: ?ct: CancellationToken -> Task<ListChannelResponse list>
  abstract member SubscribeChannelChange: ?ct: CancellationToken -> Task<IAsyncEnumerable<ChannelEventUpdate>>
