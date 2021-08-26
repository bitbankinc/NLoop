namespace LndClient.LndGrpcClient

open System
open System.IO
open System.Net.Http
open System.Runtime.CompilerServices
open System.Threading
open DotNetLightning.Payment
open DotNetLightning.Utils
open DotNetLightning.Utils.Primitives
open FSharp.Control
open Google.Protobuf
open Grpc.Core
open Grpc.Net.Client
open LndClient
open LndGrpcClient
open Lnrpc
open FSharp.Control.Tasks
open NBitcoin

module private Helpers =
  do System.Environment.SetEnvironmentVariable("GRPC_SSL_CIPHER_SUITES", "HIGH+ECDSA");


type LndGrpcSettings = {
  TlsCertLocation: string option
  Url: Uri
}

type NLoopLndGrpcClient(settings: LndGrpcSettings, network: Network, ?httpClient: HttpClient) =
  let channel =
    let opts = GrpcChannelOptions()
    httpClient
    |> Option.iter(fun c -> opts.HttpClient <- c)
    settings.TlsCertLocation
    |> Option.iter(fun certPath ->
      if settings.Url.Scheme <> "https" then
        failwith $"the grpc url must be https. it was {settings.Url.Scheme}"
      else
        opts.Credentials <-
          File.ReadAllText(certPath)
          |> SslCredentials
    )
    GrpcChannel.ForAddress(settings.Url, opts)
  let client = Lightning.LightningClient(channel)
  let invoiceClient = Invoicesrpc.Invoices.InvoicesClient(channel)
  let defaultHeaders = null
  let defaultDeadline = Nullable()
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
        let! m = client.ConnectPeerAsync(r, defaultHeaders, defaultDeadline, ct).ResponseAsync
        return m
      }
    member this.GetDepositAddress(ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let req = NewAddressRequest()
        let! m = client.NewAddressAsync(req, defaultHeaders, defaultDeadline, ct).ResponseAsync
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
        let! m = invoiceClient.AddHoldInvoiceAsync(req, defaultHeaders, defaultDeadline, ct)
        return m.PaymentRequest |> PaymentRequest.Parse |> ResultUtils.Result.deref
      }
    member this.GetInfo(ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let req = GetInfoRequest()
        let! m = client.GetInfoAsync(req, defaultHeaders, defaultDeadline, ct)
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
        let! r = client.AddInvoiceAsync(req, defaultHeaders, defaultDeadline, ct)
        return r.PaymentRequest |> PaymentRequest.Parse |> ResultUtils.Result.deref
      }
    member this.ListChannels(ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let req = ListChannelsRequest()
        let! r = client.ListChannelsAsync(req, defaultHeaders, defaultDeadline, ct)
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
        let! t = client.SendPaymentSyncAsync(req, defaultHeaders, defaultDeadline, ct)
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
          let! r = client.OpenChannelSyncAsync(req, defaultHeaders, defaultDeadline, ct)
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
        let! resp = client.QueryRoutesAsync(req, defaultHeaders, defaultDeadline, ct)
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
          .SubscribeChannelEvents(req, defaultHeaders, defaultDeadline, ct)
          .ResponseStream
          .ReadAllAsync(ct)
        |> AsyncSeq.ofAsyncEnum
        |> AsyncSeq.map(LndClient.ChannelEventUpdate.FromGrpcType)
        |> AsyncSeq.toAsyncEnum
    }
