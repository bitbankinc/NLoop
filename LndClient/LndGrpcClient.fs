namespace LndClient

open System
open System.Linq
open System.IO
open System.Net.Http
open System.Runtime.CompilerServices
open System.Security.Cryptography.X509Certificates
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils
open FSharp.Control
open Google.Protobuf
open Google.Protobuf.Collections
open Grpc.Core
open Grpc.Net.Client
open Invoicesrpc
open LndClient
open Lnrpc
open FSharp.Control.Tasks
open NBitcoin
open NBitcoin.DataEncoders
open FsToolkit.ErrorHandling
open Routerrpc

type LndGrpcSettings = internal {
  Url: Uri
  MaybeCertificateThumbprint: byte[] option
  Macaroon: MacaroonInfo option
  AllowInsecure: bool
}
  with
  static member Create(uriStr: string,
                       macaroon: string option,
                       macaroonFile: string option,
                       maybeCertificateThumbprint: string option,
                       allowInsecure) = result {
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

    let! certThumbprint =
      match maybeCertificateThumbprint with
      | None -> Ok None
      | Some t ->
        try
          t.Replace(":", "")
          |> Encoders.Hex.DecodeData
          |> Some
          |> Ok
        with
        | ex ->
          $"%A{ex}"
          |> Error

    return
      { Url = uri
        Macaroon = macaroonInfo
        AllowInsecure = allowInsecure
        MaybeCertificateThumbprint = certThumbprint
        }
  }

  member this.CreateHttpClientHandler() =
    let handler = new HttpClientHandler()
    if this.AllowInsecure && this.Url.Scheme = "http" then
      handler
    elif this.MaybeCertificateThumbprint.IsNone && this.Url.Scheme = "https" then
      handler
    else
      let updateHandler (h: HttpClientHandler) =
        this.MaybeCertificateThumbprint
        |> Option.iter(fun x ->
          h.ServerCertificateCustomValidationCallback <-
            let cb = fun _request _cert (chain: X509Chain) _errors ->
              let actualCert = chain.ChainElements.[chain.ChainElements.Count - 1].Certificate
              let hash = getHash(actualCert)
              hash.SequenceEqual(x)
            Func<_,_,_,_,_>(cb)
        )
        if this.AllowInsecure then
          h.ServerCertificateCustomValidationCallback <-
            let cb = fun _ _ _ _ -> true
            Func<_,_,_,_,_>(cb)
        elif this.Url.Scheme = "http" then
          raise <| InvalidOperationException("AllowInsecure is set to false, but the URI is not using https")
        h

      handler |> updateHandler

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

  [<Extension>]
  static member ApplyLndSettings(this: GrpcChannelOptions, settings: LndGrpcSettings) =
    this.HttpHandler <- settings.CreateHttpClientHandler()
    match settings.Macaroon with
    | Some _ when settings.Url.Scheme <> "https" ->
      failwith $"The grpc url must be https when using certificate. It was {settings.Url.Scheme}"
    | Some _ ->
      this.Credentials <-
        let callCred = CallCredentials.FromInterceptor(fun ctx metadata -> unitTask {
          settings.CreateLndAuth() |> metadata.AddLndAuthentication
        })
        ChannelCredentials.Create(SslCredentials(), callCred)
    | None ->
      ()

/// grpc-dotnet does not support specifying custom ssl credential which is necessary in case of using LND securely.
/// ref: https://github.com/grpc/grpc/issues/21554
/// So we must set custom HttpMessageHandler for HttpClient which performs validation.
/// But if you pass `HttpClient` in constructor, the handler of this HttpClient instance will override the
type NLoopLndGrpcClient(settings: LndGrpcSettings, network: Network) =
  let channel =
    let opts = GrpcChannelOptions()
    opts.ApplyLndSettings(settings)
    GrpcChannel.ForAddress(settings.Url, opts)
  let client =
    Lightning.LightningClient(channel)
  let invoiceClient = Invoices.InvoicesClient(channel)
  let routerClient = Router.RouterClient(channel)

  member this.DefaultHeaders = null
    //let metadata = Metadata()
    // metadata.Add()
    // metadata

  member this.Deadline =
    Nullable(DateTime.UtcNow + TimeSpan.FromSeconds(20.))

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
        let req = AddHoldInvoiceRequest()
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

    member this.SubscribeSingleInvoice(invoiceHash, ct) =
      let ct = defaultArg ct CancellationToken.None
      let resp =
        let req = SubscribeSingleInvoiceRequest()
        req.RHash <- invoiceHash.ToBytes() |> ByteString.CopyFrom
        invoiceClient.SubscribeSingleInvoice(req, this.DefaultHeaders, this.Deadline, ct).ResponseStream
      let translateEnum: Invoice.Types.InvoiceState -> InvoiceStateEnum =
        function
        | Invoice.Types.InvoiceState.Accepted -> InvoiceStateEnum.Accepted
        | Invoice.Types.InvoiceState.Canceled -> InvoiceStateEnum.Canceled
        | Invoice.Types.InvoiceState.Open -> InvoiceStateEnum.Open
        | Invoice.Types.InvoiceState.Settled -> InvoiceStateEnum.Settled
        | _ -> raise <| InvalidDataException("Enum out of range.")

      resp.ReadAllAsync(ct)
      |> AsyncSeq.ofAsyncEnum
      |> AsyncSeq.map(fun inv ->
        { InvoiceSubscription.InvoiceState = inv.State |> translateEnum
          AmountPayed = inv.AmtPaidSat |> Money.Satoshis
          PaymentRequest = inv.PaymentRequest |> PaymentRequest.Parse |> ResultUtils.Result.deref }
        )

    member this.GetInvoice(paymentPreimage, amount, expiry, memo, ct) =
      task {
        let req = Invoice()
        let ct = defaultArg ct CancellationToken.None
        req.RPreimage <- paymentPreimage.ToByteArray() |> ByteString.CopyFrom
        req.Value <- amount.Satoshi
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
    member this.Offer(param, ct) =
      let ct = defaultArg ct CancellationToken.None
      task {
        let responseStream =
          let req = SendPaymentRequest()
          req.PaymentRequest <- param.Invoice.ToString()
          req.OutgoingChanIds.AddRange(param.OutgoingChannelIds |> Seq.map(fun c -> c.ToUInt64()))
          req.FeeLimitSat <- param.MaxFee.Satoshi
          routerClient.SendPaymentV2(req).ResponseStream

        let f (s:Payment) =
          {
            PaymentResult.Fee = s.FeeMsat |> LNMoney.MilliSatoshis
            PaymentPreimage = s.PaymentPreimage |> hex.DecodeData |> PaymentPreimage.Create
          }

        try
          let mutable result = None
          let mutable hasNotFinished = true
          while result.IsNone && hasNotFinished do
            ct.ThrowIfCancellationRequested()
            let! notFinished = responseStream.MoveNext(ct)
            hasNotFinished <- notFinished
            if hasNotFinished then
              let status = responseStream.Current
              match status.Status with
              | Payment.Types.PaymentStatus.Succeeded ->
                result <- status |> f |> Ok |> Some
              | Payment.Types.PaymentStatus.Failed ->
                result <- status.FailureReason |> Enum.GetName |> Error |> Some
              | s ->
                result <- $"Unexpected payment state: {s}" |> Error |> Some
          return
            match result with
            | Some r -> r
            | None -> Error $"Empty result in offer"
        with
        | ex ->
          return Error $"Unexpected error while sending offchain offer: {ex.ToString()}"
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
    member this.SubscribeChannelChange(ct) =
      let ct = defaultArg ct CancellationToken.None
      let req = ChannelEventSubscription()
      client
        .SubscribeChannelEvents(req, this.DefaultHeaders, this.Deadline, ct)
        .ResponseStream
        .ReadAllAsync(ct)
      |> AsyncSeq.ofAsyncEnum
      |> AsyncSeq.map(LndClient.ChannelEventUpdate.FromGrpcType)
