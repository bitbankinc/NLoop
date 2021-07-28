namespace LndClient

open System
open System.IO
open System.Net.Http
open System.Runtime.CompilerServices
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
open System.Net
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

type ILightningInvoiceListener =
  inherit IDisposable
  abstract member WaitInvoice: ct : CancellationToken -> Task<PaymentRequest>

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


type ListChannelResponse = {
  Id: ShortChannelId
  Cap: Money
  LocalBalance: Money
}
type INLoopLightningClient =
  abstract member GetDepositAddress: unit -> Task<BitcoinAddress>
  abstract member GetHodlInvoice:
    paymentHash: Primitives.PaymentHash *
    value: LNMoney *
    expiry: TimeSpan *
    memo: string
      -> Task<PaymentRequest>
  abstract member GetInvoice:
    paymentPreimage: PaymentPreimage *
    amount: LNMoney *
    expiry: TimeSpan *
    memo: string -> Task<PaymentRequest>
  abstract member Offer: invoice: PaymentRequest -> Task<Result<Primitives.PaymentPreimage, string>>
  abstract member Listen: unit -> Task<ILightningInvoiceListener>
  abstract member GetInfo: unit -> Task<obj>
  abstract member QueryRoutes: nodeId: PubKey * amount: LNMoney -> Task<Route>
  abstract member OpenChannel: request: LndOpenChannelRequest -> Task<Result<unit, LndOpenChannelError>>
  abstract member ConnectPeer: nodeId: PubKey * host: string -> Task
  abstract member ListChannels: unit -> Task<ListChannelResponse list>
