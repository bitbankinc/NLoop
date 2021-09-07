namespace LndClient

open System
open System.IO
open System.Net.Http
open System.Runtime.CompilerServices
open System.Threading
open DotNetLightning.Payment
open DotNetLightning.Utils
open FSharp.Control
open Google.Protobuf
open Grpc.Core
open Grpc.Net.Client
open Grpc.Net.Client.Configuration
open LndClient
open LndClient
open Lnrpc
open FSharp.Control.Tasks
open NBitcoin
open NBitcoin.DataEncoders
open FsToolkit.ErrorHandling

type LndGrpcSettings = {
  TlsCertLocation: string option
  Url: Uri
  Macaroon: MacaroonInfo option
}
  with
  static member Create(uriStr: string, tlsCertPath: string option, macaroon: string option, macaroonFile: string option) = result {
    let! uri = uriStr |> parseUri
    let! macaroonInfo =
      match macaroon, macaroonFile with
      | Some _, Some _ ->
        Error "You cannot specify both raw macaroon and macaroon file"
      | Some x, _ ->
        x
        |> parseMacaroon
        |> Result.map(MacaroonInfo.Raw >> Some)
      | _, Some x ->
        if x.EndsWith(".macaroon", StringComparison.OrdinalIgnoreCase) |> not then
          Error $"macaroon file must end with \".macaroon\", it was {x}"
        else
          MacaroonInfo.FilePath x
          |> Some
          |> Ok
      | None, None ->
        None |> Ok

    return
      { Url = uri
        Macaroon = macaroonInfo
        TlsCertLocation = tlsCertPath }
  }

  member this.CreateLndAuth() =
    match this.Macaroon with
    | Some (MacaroonInfo.Raw m) ->
      m
      |> LndAuth.FixedMacaroon
    | Some (MacaroonInfo.FilePath m) when m |> String.IsNullOrEmpty |> not ->
      m
      |> LndAuth.MacaroonFile
    | _ ->
      LndAuth.Null

[<AbstractClass;Sealed;Extension>]
type GrpcClientExtensions =

  [<Extension>]
  static member AddLndAuthentication(this: Metadata, lndAuth: LndAuth) =
    match lndAuth with
    | LndAuth.FixedMacaroon macaroon ->
      let macaroonHex = macaroon.SerializeToBytes() |> Encoders.Hex.EncodeData
      this.Add("macaroon", macaroonHex)
    | LndAuth.MacaroonFile filePath ->
      if not <| filePath.EndsWith(".macaroon", StringComparison.OrdinalIgnoreCase) then
        raise <| ArgumentException($"filePath ({filePath}) is not a macaroon file", nameof(filePath))
      else
        let macaroonHex = filePath |> File.ReadAllBytes |> Encoders.Hex.EncodeData
        this.Add("macaroon", macaroonHex)
    | LndAuth.Null -> ()


[<RequireQualifiedAccess>]
module GrpcChannelOptions =
  let addCred (certPath: string) (opts: GrpcChannelOptions) =
    failwith "TODO"


type NLoopLndGrpcClient(settings: LndGrpcSettings, network: Network, ?httpClient: HttpClient) =
  let channel =
    let opts = GrpcChannelOptions()
    httpClient
    |> Option.iter(fun c -> opts.HttpClient <- c)
    do
      match settings.Macaroon, settings.TlsCertLocation with
      | None, None ->
        opts.Credentials <- ChannelCredentials.Insecure
      | Some _macaroon, Some certPath ->
        if settings.Url.Scheme <> "https" then
          failwith $"the grpc url must be https. it was {settings.Url.Scheme}"
        else
          let sslCred =
            File.ReadAllText(certPath)
            |> SslCredentials
          let callCred =
            CallCredentials.FromInterceptor(fun ctx metadata -> unitTask {
              settings.CreateLndAuth()
              |> metadata.AddLndAuthentication
            })
          opts.Credentials <- ChannelCredentials.Create(sslCred, callCred)
      | None, Some _
      | Some _ , None _ ->
        failwith $"You must specify both TLS certification and macaroon for lnd. Unless --allowinsecure is on."
    GrpcChannel.ForAddress(settings.Url, opts)
  let client =
    Lightning.LightningClient(channel)
  let invoiceClient = Invoicesrpc.Invoices.InvoicesClient(channel)
  member this.DefaultHeaders = null
    //let metadata = Metadata()
    // metadata.Add()
    // metadata

  member this.Deadline =
    Nullable(DateTime.Now + TimeSpan.FromSeconds(20.))

  interface INLoopLightningClient with
    member this.ConnectPeer(nodeId, host, ct) =
      let ct = defaultArg ct CancellationToken.None
      let r = ConnectPeerRequest()
      r.Addr <-
        let addr = LightningAddress()
        addr.Host <- host
        addr.Pubkey <- nodeId.ToHex()
        addr
      unitTask {
        let! m = client.ConnectPeerAsync(r, this.DefaultHeaders, this.Deadline, ct).ResponseAsync
        return m
      }
    member this.GetDepositAddress(ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let req = NewAddressRequest()
        let! m = client.NewAddressAsync(req, this.DefaultHeaders, this.Deadline, ct).ResponseAsync
        return BitcoinAddress.Create(m.Address, network)
      }
    member this.GetHodlInvoice(paymentHash, value, expiry, memo, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let req = Invoicesrpc.AddHoldInvoiceRequest()
        req.Hash <-
          paymentHash.ToBytes()
          |> ByteString.CopyFrom
        req.Value <- value.Satoshi
        req.Expiry <- expiry.Seconds |> int64
        req.Memo <- memo
        let! m = invoiceClient.AddHoldInvoiceAsync(req, this.DefaultHeaders, this.Deadline, ct)
        return m.PaymentRequest |> PaymentRequest.Parse |> ResultUtils.Result.deref
      }
    member this.GetInfo(ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let req = GetInfoRequest()
        let! m = client.GetInfoAsync(req, this.DefaultHeaders, this.Deadline, ct)
        return m |> box
      }
    member this.GetInvoice(paymentPreimage, amount, expiry, memo, ct) =
      task {
        let req = Invoice()
        let ct = defaultArg ct CancellationToken.None
        req.RPreimage <- paymentPreimage.ToByteArray() |> ByteString.CopyFrom
        req.AmtPaidSat <- amount.Satoshi
        req.Expiry <- expiry.Seconds |> int64
        req.Memo <- memo
        let! r = client.AddInvoiceAsync(req, this.DefaultHeaders, this.Deadline, ct)
        return r.PaymentRequest |> PaymentRequest.Parse |> ResultUtils.Result.deref
      }
    member this.ListChannels(ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let req = ListChannelsRequest()
        let! r = client.ListChannelsAsync(req, this.DefaultHeaders, this.Deadline, ct)
        return
          r.Channels
          |> Seq.map(fun c ->
            { ListChannelResponse.Id = c.ChanId |> ShortChannelId.FromUInt64
              Cap = c.Capacity |> Money.Satoshis
              LocalBalance = c.LocalBalance |> Money.Satoshis
              NodeId = c.RemotePubkey |> PubKey
            })
          |> Seq.toList
      }
    member this.Offer(invoice, ct) =
      let ct = defaultArg ct CancellationToken.None
      task {
        let req = SendRequest()
        req.PaymentRequest <- invoice.ToString()
        let! t = client.SendPaymentSyncAsync(req, this.DefaultHeaders, this.Deadline, ct)
        if t.PaymentError |> String.IsNullOrEmpty then
          return t.PaymentPreimage |> PaymentPreimage.Create |> Ok
        else
          return t.PaymentError |> Error
      }
    member this.OpenChannel(request, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let req = OpenChannelRequest()
        req.Private <- request.Private |> Option.defaultValue true
        req.CloseAddress <- request.CloseAddress |> Option.toObj
        req.NodePubkey <- request.NodeId.ToBytes() |> ByteString.CopyFrom
        req.LocalFundingAmount <- request.Amount.Satoshi
        try
          let! r = client.OpenChannelSyncAsync(req, this.DefaultHeaders, this.Deadline, ct)
          return r.ToOutPoint() |> Ok
        with
        | :? RpcException as e ->
          return
            {
              StatusCode = e.StatusCode |> int |> Some
              Message = e.Message
            }
            |> Error
      }
    member this.QueryRoutes(nodeId, amount, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let req = QueryRoutesRequest()
        req.PubKey <- nodeId.ToHex()
        req.Amt <- amount.Satoshi
        let! resp = client.QueryRoutesAsync(req, this.DefaultHeaders, this.Deadline, ct)
        let r = resp.Routes.[0]
        return
          r.Hops
          |> Seq.map(fun t ->
            { RouteHop.Fee = t.FeeMsat |> LNMoney.MilliSatoshis
              PubKey = t.PubKey |> PubKey
              ShortChannelId = t.ChanId |> ShortChannelId.FromUInt64
              CLTVExpiryDelta = t.Expiry
            }
          )
          |> Seq.toList
          |> Route.Route
      }
    member this.SubscribeChannelChange(ct) = task {
      let ct = defaultArg ct CancellationToken.None
      let req = ChannelEventSubscription()
      return
        client
          .SubscribeChannelEvents(req, this.DefaultHeaders, this.Deadline, ct)
          .ResponseStream
          .ReadAllAsync(ct)
        |> AsyncSeq.ofAsyncEnum
        |> AsyncSeq.map(LndClient.ChannelEventUpdate.FromGrpcType)
        |> AsyncSeq.toAsyncEnum
    }
