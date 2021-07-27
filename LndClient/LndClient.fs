namespace LndClient

open FSharp.Control.Tasks

open System.Threading
open System.Threading.Channels
open DotNetLightning.Payment
open SwaggerProvider
open DotNetLightning.Utils
open System
open System.Linq
open NBitcoin
open NBitcoin.DataEncoders
open System.Net.Http
open System.Security.Authentication
open System.Security.Cryptography.X509Certificates
open System.Net

type O = OptionalArgumentAttribute

[<AutoOpen>]
module LndInvoiceSwaggerClient =
  let [<Literal>] LND_VERSION = "v0.12.1-beta"
  let [<Literal>] InvoiceRPCSchema =
    "https://raw.githubusercontent.com/lightningnetwork/lnd/v0.12.1-beta/lnrpc/invoicesrpc/invoices.swagger.json"
  type LndInvoiceSpecProvider = OpenApiClientProvider<InvoiceRPCSchema>

  let [<Literal>] BaseRPCSchema =
    "https://raw.githubusercontent.com/lightningnetwork/lnd/v0.12.1-beta/lnrpc/rpc.swagger.json"

  type LndBaseRpcSpecProvider = OpenApiClientProvider<BaseRPCSchema>

  let private getHash (cert: X509Certificate2) =
    use alg = System.Security.Cryptography.SHA256.Create()
    alg.ComputeHash(cert.RawData)

  let private createHttpClient(settings: LndRestSettings, defaultHttpClient: HttpClient option) : HttpClient =
    match defaultHttpClient with
    | Some x when settings.AllowInsecure && settings.Uri.Scheme = "http" ->
      x
    | Some x when settings.MaybeCertificateThumbprint.IsNone && settings.Uri.Scheme = "https" ->
      x
    | _ ->

      let handler = new HttpClientHandler()
      handler.SslProtocols <- SslProtocols.Tls12

      settings.MaybeCertificateThumbprint
      |> Option.iter(fun x ->
        handler.ServerCertificateCustomValidationCallback <-
          let cb = fun _request _cert (chain: X509Chain) _errors ->
            let actualCert = chain.ChainElements.[chain.ChainElements.Count - 1].Certificate
            let hash = getHash(actualCert)
            hash.SequenceEqual(x)
          Func<_,_,_,_,_>(cb)
      )

      if (settings.AllowInsecure) then
        handler.ServerCertificateCustomValidationCallback <-
        let cb = fun _ _ _ _ -> true
        Func<_,_,_,_,_>(cb)
      else if settings.Uri.Scheme = "http" then
        raise <| InvalidOperationException("AllowInsecure is set to false, but the URI is not using https")
      new HttpClient(handler)

  type LndTypeProviderClient(network: Network, restSettings: LndRestSettings, ?defaultHttpClient: HttpClient) =

    let httpClient = createHttpClient(restSettings, defaultHttpClient)
    do
      let auth = restSettings.CreateLndAuth()
      httpClient.AddLndAuthentication(auth)
      httpClient.BaseAddress <-
        let b = UriBuilder(restSettings.Uri)
        b.UserName <- ""
        b.Password <- ""
        b.Uri

    let invoiceCli = LndInvoiceSpecProvider.Client(httpClient)
    let baseRPCClient = LndBaseRpcSpecProvider.Client(httpClient)

    member private this.GetPaymentPreimage(paymentHash: string) = task {
      let! p = baseRPCClient.ListPayments().ConfigureAwait false
      return
        p.Payments
        |> Seq.filter(fun i -> i.PaymentHash = paymentHash)
        |> Seq.tryExactlyOne
        |> Option.map(fun i -> i.PaymentPreimage)
    }

    interface INLoopLightningClient with
      member this.GetDepositAddress() = task {
        let! resp =  baseRPCClient.NewAddress().ConfigureAwait false
        return BitcoinAddress.Create(resp.Address, network)
      }

      member this.Offer(invoice: PaymentRequest) = task {
        let! resp =
          let req = LndBaseRpcSpecProvider.lnrpcSendRequest()
          req.PaymentRequest <- invoice.ToString()
          baseRPCClient.SendPaymentSync(req).ConfigureAwait false
        if resp.PaymentError |> String.IsNullOrEmpty && resp.PaymentPreimage |> isNull |> not then
          return
            resp.PaymentPreimage
            |> PaymentPreimage.Create
            |> Ok
        elif resp.PaymentError = "invoice is already paid" then
          let! maybePreimage = this.GetPaymentPreimage(invoice.PaymentHash.Value.ToString())
          let hex = HexEncoder()
          return
            maybePreimage.Value
            |> hex.DecodeData
            |> PaymentPreimage.Create
            |> Ok
        else
          return
            resp.PaymentError
            |> Error
      }

      member this.Listen() =
        failwith "todo"

      member this.GetHodlInvoice(paymentHash: PaymentHash, value: LNMoney, expiry: TimeSpan, memo: string) = task {
        let! resp =
          let req =
            LndInvoiceSpecProvider.invoicesrpcAddHoldInvoiceRequest()
          req.Hash <- paymentHash.Value.ToBytes()
          req.ValueMsat <- value.MilliSatoshi.ToString()
          req.Expiry <- expiry.Seconds.ToString()
          req.Memo <- memo
          invoiceCli.AddHoldInvoice(req)
        return
          resp.PaymentRequest
          |> PaymentRequest.Parse
          |> ResultUtils.Result.deref
      }

      member this.GetInvoice(preimage: PaymentPreimage, amount: LNMoney, expiry: TimeSpan, memo: string) = task {
        let! resp =
          let req = LndBaseRpcSpecProvider.lnrpcInvoice()
          req.ValueMsat <- amount.MilliSatoshi.ToString()
          req.Expiry <- expiry.Seconds.ToString()
          req.RPreimage <- preimage.ToByteArray()
          req.Memo <- memo
          baseRPCClient.AddInvoice(req)
        return
          resp.PaymentRequest
          |> PaymentRequest.Parse
          |> ResultUtils.Result.deref
      }

      member this.GetInfo() = task {
        let! r = baseRPCClient.GetInfo()
        return r |> box
      }

      member this.QueryRoutes(nodeId: PubKey, amount: LNMoney, numRoutes) = task {
        let! resp = baseRPCClient.QueryRoutes(nodeId.ToHex(), amount.Satoshi.ToString(), numRoutes.ToString())
        let hops =
          resp.Routes
          |> Seq.head
          |> fun x -> x.Hops
          |> Array.map(fun hop -> {
            RouteHop.PubKey = hop.PubKey |> PubKey
            ShortChannelId = hop.ChanId |> ShortChannelId.ParseUnsafe
            Fee = hop.Fee |> int64 |> LNMoney.MilliSatoshis
            CLTVExpiryDelta =
              hop.Expiry
              |> Option.defaultValue 1L
              |> uint32
          })
          |> List.ofArray

        return Route hops
      }

      member this.OpenChannel(openChannelRequest) = task {
        let exnToError(baseEx: exn) =
          match baseEx with
          | :? HttpRequestException as ex ->
            {
              LndOpenChannelError.StatusCode = ex.StatusCode |> Option.ofNullable |> Option.map(int)
              Message = ex.ToString()
            }
          | :? AggregateException as e when (e.InnerException :? HttpRequestException) ->
            let ex = e.InnerException :?> HttpRequestException
            {
              LndOpenChannelError.StatusCode = ex.StatusCode |> Option.ofNullable |> Option.map(int)
              Message = ex.ToString()
            }
          | ex ->
            {
              LndOpenChannelError.StatusCode = None
              Message = ex.ToString()
            }

        try
          let! resp = 
            let req = LndBaseRpcSpecProvider.lnrpcOpenChannelRequest()
            req.Private <- openChannelRequest.Private
            req.CloseAddress <- openChannelRequest.CloseAddress |> Option.toObj
            req.NodePubkeyString <- openChannelRequest.NodeId.ToHex()
            req.NodePubkey <- openChannelRequest.NodeId.ToBytes()
            req.LocalFundingAmount <- openChannelRequest.Amount.Satoshi.ToString()
            baseRPCClient.OpenChannel(req)
          if (resp.Error |> isNull) then
            return () |> Ok
          else
            return
              {
                LndOpenChannelError.StatusCode = resp.Error.HttpCode
                Message = resp.Error.Message
              }
              |> Error
        with
        | ex ->
          return
            exnToError ex
            |> Error
      }

      member this.ConnectPeer(pubKey: PubKey, host: string) = unitTask {
        let req = LndBaseRpcSpecProvider.lnrpcConnectPeerRequest()
        req.Addr <-
          let addr = LndBaseRpcSpecProvider.lnrpcLightningAddress()
          addr.Pubkey <-  pubKey.ToHex()
          addr.Host <- host
          addr
        return! baseRPCClient.ConnectPeer(req)
      }