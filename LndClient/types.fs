namespace LndClient

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Runtime.CompilerServices
open System.Threading.Channels
open DotNetLightning.Channel
open Macaroons
open NBitcoin.DataEncoders
open System.Threading
open System.Threading.Tasks
open NBitcoin
open DotNetLightning.Utils
open DotNetLightning.Payment
open System.Net.Http.Headers

[<RequireQualifiedAccess>]
type MacaroonInfo =
  | Raw of Macaroon
  | FilePath of string

[<RequireQualifiedAccess>]
type LndAuth=
  | FixedMacaroon of Macaroon
  | MacaroonFile of string
  | Null

[<AbstractClass;Sealed;Extension>]
type HttpClientExtensions =

  [<Extension>]
  static member AddLndAuthentication(this: HttpRequestHeaders, lndAuth: LndAuth) =
    match lndAuth with
    | LndAuth.FixedMacaroon macaroon ->
      let macaroonHex = macaroon.SerializeToBytes() |> Encoders.Hex.EncodeData
      this.Add("Grpc-Metadata-macaroon", macaroonHex)
    | LndAuth.MacaroonFile filePath ->
      if not <| filePath.EndsWith(".macaroon", StringComparison.OrdinalIgnoreCase) then
        raise <| ArgumentException($"filePath ({filePath}) is not a macaroon file", nameof(filePath))
      else
        let macaroonHex = filePath |> File.ReadAllBytes |> Encoders.Hex.EncodeData
        this.Add("Grpc-Metadata-macaroon", macaroonHex)
    | LndAuth.Null -> ()

  [<Extension>]
  static member AddLndAuthentication(this: HttpClient, lndAuth: LndAuth) =
    this.DefaultRequestHeaders.AddLndAuthentication(lndAuth)

  [<Extension>]
  static member AddLndAuthentication(httpRequestMessage: HttpRequestMessage, lndAuth) =
    httpRequestMessage.Headers.AddLndAuthentication(lndAuth)

open FsToolkit.ErrorHandling
[<AutoOpen>]
module private Helpers =
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

type LndRestSettings = internal {
  Uri: Uri
  MaybeCertificateThumbprint: byte[] option
  Macaroon: MacaroonInfo
  AllowInsecure: bool
}
  with
  static member Create(uriStr: string, certThumbPrintHex: string option, macaroon: string option, macaroonFile: string option, allowInsecure) = result {
    let! uri = uriStr |> parseUri
    let! macaroonInfo =
      match macaroon, macaroonFile with
      | Some _, Some _ ->
        Error "You cannot specify both raw macaroon and macaroon file"
      | Some x, _ ->
        x
        |> parseMacaroon
        |> Result.map(MacaroonInfo.Raw)
      | _, Some x ->
        if x.EndsWith(".macaroon", StringComparison.OrdinalIgnoreCase) |> not then
          Error $"macaroon file must end with \".macaroon\", it was {x}"
        else
          MacaroonInfo.FilePath x
          |> Ok
      | None, None ->
        Error "You must specify either macaroon itself or path to the macaroon file"

    let! certThumbPrint =
        match certThumbPrintHex with
        | None ->
          Ok None
        | Some cert ->
          try
            cert.Replace(":", "")
            |> Encoders.Hex.DecodeData
            |> Some
            |> Ok
          with
          | ex ->
            sprintf "%A" ex
            |> Error

    return
      { Uri = uri
        MaybeCertificateThumbprint = certThumbPrint
        AllowInsecure = allowInsecure
        Macaroon = macaroonInfo }
  }

  member this.CreateLndAuth() =
    match this.Macaroon with
    | MacaroonInfo.Raw m ->
      m
      |> LndAuth.FixedMacaroon
    | MacaroonInfo.FilePath m when m |> String.IsNullOrEmpty |> not ->
      m
      |> LndAuth.MacaroonFile
    | _ ->
      LndAuth.Null


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
  abstract member Offer: invoice: PaymentRequest * ?ct: CancellationToken -> Task<Result<Primitives.PaymentPreimage, string>>
  abstract member GetInfo: ?ct: CancellationToken  -> Task<obj>
  abstract member QueryRoutes: nodeId: PubKey * amount: LNMoney * ?ct: CancellationToken -> Task<Route>
  abstract member OpenChannel: request: LndOpenChannelRequest * ?ct: CancellationToken -> Task<Result<OutPoint, LndOpenChannelError>>
  abstract member ConnectPeer: nodeId: PubKey * host: string * ?ct: CancellationToken -> Task
  abstract member ListChannels: ?ct: CancellationToken -> Task<ListChannelResponse list>
  abstract member SubscribeChannelChange: ?ct: CancellationToken -> Task<IAsyncEnumerable<ChannelEventUpdate>>
