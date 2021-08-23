namespace LndClient

open System
open System.IO
open System.Net.Http
open System.Net.WebSockets
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils
open FSharp.Control.Tasks
open LndSwaggerClient.Base
open LndSwaggerClient.Invoice
open NBitcoin
open NBitcoin.DataEncoders

[<AutoOpen>]
module private Helpers =
  let withCancellation(t, ct: CancellationToken) =
    use delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
    let waiting = Task.Delay(-1, delayCts.Token)
    let doing = t
    task {
      let! _ = Task.WhenAny(waiting, doing)
      delayCts.Cancel()
      ct.ThrowIfCancellationRequested()
      return! doing
    }
type WebSocketRawListener<'T>(httpClient: HttpClient, subPath: string, auth: LndAuth, ct: CancellationToken) as this =
  let channel: Channel<'T> = Channel.CreateBounded<_>(2)
  let mutable listenLoop: Task = null
  let cts = new CancellationTokenSource()

  do this.StartLoop()
  member private this.StartLoop() =
    try
      let req =
        let url = withTrailingSlash(httpClient.BaseAddress.ToString()) + subPath
        new HttpRequestMessage(HttpMethod.Get, url)
      do
        req.AddLndAuthentication(auth)
      use ws = new ClientWebSocket()
      listenLoop <- unitTask {
        try
          try
            let! resp = httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
            let! body = resp.Content.ReadAsStreamAsync()
            use reader = new StreamReader(body)
            while not <| ct.IsCancellationRequested do
              let! line =
                let segment = Array.zeroCreate DefaultBufferSize |> Memory
                withCancellation(reader.ReadLineAsync(), cts.Token)
              if line <> null then
                match line with
                | l when l.StartsWith("{\"result\":", StringComparison.OrdinalIgnoreCase) ->
                  let json = System.Text.Json.JsonDocument.Parse(l)
                  let invoiceStr = json.RootElement.EnumerateObject() |> Seq.find(fun r -> r.Name = "result")
                  ()
                | l when l.StartsWith("{\"error\":", StringComparison.OrdinalIgnoreCase) ->
                  ()
                | _ -> ()
          with
          | _ when cts.IsCancellationRequested -> ()
          | ex ->
            channel.Writer.TryComplete()
            |> ignore
        finally
          this.Dispose(false)
      }
    with
    | _ex ->
      (this :> IDisposable).Dispose()

  member this.Dispose(waitLoop: bool) =
    if ct.IsCancellationRequested then () else
    ()

  interface IListener<'T> with
    member val Reader = channel.Reader

  interface IDisposable with
    member this.Dispose() =
      this.Dispose(true)

type WebSocketListener<'T>() as this =
  let channel = Channel.CreateBounded(2)
  let mutable listenLoop = null

  do this.StartLoop()
  member this.StartLoop() =
    listenLoop <- unitTask {
      while true do
        return ()
    }


type LndNSwagClient(network: Network, restSettings: LndRestSettings, ?defaultHttpClient: HttpClient) =
  let httpClient = createHttpClient(restSettings, defaultHttpClient)
  let url =
      let b = UriBuilder(restSettings.Uri)
      b.UserName <- ""
      b.Password <- ""
      b.Uri.ToString()
  let auth = restSettings.CreateLndAuth()
  do
    httpClient.AddLndAuthentication(auth)
  let invoiceRpcClient = LndSwaggerInvoiceClient(url, httpClient)
  let baseRpcClient = LndSwaggerBaseClient(url, httpClient)

  member val InvoiceRpcClient = invoiceRpcClient with get
  member val Auth = auth with get
  member val BaseRpcClient = baseRpcClient with get

  member internal this.GetFullUri(relativePath: string) =
    restSettings.Uri.AbsoluteUri
    |> withTrailingSlash
    |> (+) <| relativePath

  member private this.GetPaymentPreimage(paymentHash: string, ct: CancellationToken) = task {
    let! p = baseRpcClient.ListPaymentsAsync(Nullable(), null, null, Nullable(), ct).ConfigureAwait false
    return
      p.Payments
      |> Seq.filter(fun i -> i.Payment_hash = paymentHash)
      |> Seq.tryExactlyOne
      |> Option.map(fun i -> i.Payment_preimage)
  }

  interface INLoopLightningClient with
    member this.ConnectPeer(nodeId, host, ?ct) = unitTask {
      let ct = defaultArg ct CancellationToken.None
      let req = LnrpcConnectPeerRequest()
      req.Addr <-
        let addr = LnrpcLightningAddress()
        addr.Host <- host
        addr.Pubkey <- nodeId.ToHex()
        addr
      let! _ = baseRpcClient.ConnectPeerAsync(req, ct).ConfigureAwait false
      return ()
    }
    member this.GetDepositAddress(ct) = task {
      let ct = defaultArg ct CancellationToken.None
      let! a = baseRpcClient.NewAddressAsync(Nullable(), null, ct)
      return BitcoinAddress.Create(a.Address, network)
    }
    member this.GetHodlInvoice(paymentHash, value, expiry, memo, ct) = task {
      let ct = defaultArg ct CancellationToken.None
      let! resp =
        let req = InvoicesrpcAddHoldInvoiceRequest()
        req.Hash <- paymentHash.ToBytes()
        req.Value_msat <- value.MilliSatoshi.ToString()
        req.Expiry <- expiry.Seconds.ToString()
        req.Memo <- memo
        invoiceRpcClient.AddHoldInvoiceAsync(req, ct).ConfigureAwait false
      return
        resp.Payment_request
        |> PaymentRequest.Parse
        |> ResultUtils.Result.deref
    }

    member this.GetInfo(?ct) = task {
      let ct = defaultArg ct CancellationToken.None
      let! r = baseRpcClient.GetInfoAsync(ct).ConfigureAwait false
      return r |> box
    }

    member this.GetInvoice(paymentPreimage, amount, expiry, memo, ?ct) = task {
      let ct = defaultArg ct CancellationToken.None
      let! resp =
        let req = LndSwaggerClient.Base.LnrpcInvoice()
        req.R_preimage <- paymentPreimage.ToByteArray()
        req.Value_msat <- amount.MilliSatoshi.ToString()
        req.Expiry <- expiry.Seconds.ToString()
        req.Memo <- memo
        baseRpcClient.AddInvoiceAsync(req, ct).ConfigureAwait false
      return
        resp.Payment_request
        |> PaymentRequest.Parse
        |> ResultUtils.Result.deref
    }
    member this.Listen(?ct) = task {
      let ct = defaultArg ct CancellationToken.None
      return failwith "todo"
    }

    member this.Offer(invoice, ?ct) = task {
      let ct = defaultArg ct CancellationToken.None
      let! resp =
        let req = LnrpcSendRequest()
        req.Payment_request <- invoice.ToString()
        baseRpcClient.SendPaymentSyncAsync(req, ct).ConfigureAwait false
      if resp.Payment_error |> String.IsNullOrEmpty && resp.Payment_preimage |> isNull |> not then
        return
          resp.Payment_preimage
          |> PaymentPreimage.Create
          |> Ok
      elif resp.Payment_error = "invoice is already paid" then
        let! maybePreimage = this.GetPaymentPreimage(invoice.PaymentHash.Value.ToString(), ct)
        let hex = HexEncoder()
        return
          maybePreimage.Value
          |> hex.DecodeData
          |> PaymentPreimage.Create
          |> Ok
      else
        return
          resp.Payment_error
          |> Error
    }
    member this.OpenChannel(openChannelRequest, ?ct) = task {
      let ct = defaultArg ct CancellationToken.None
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
          let req = LnrpcOpenChannelRequest()
          req.Private <- openChannelRequest.Private |> Option.toNullable
          req.Close_address <- openChannelRequest.CloseAddress |> Option.toObj
          req.Node_pubkey_string <- openChannelRequest.NodeId.ToHex()
          req.Node_pubkey <- openChannelRequest.NodeId.ToBytes()
          req.Local_funding_amount <- openChannelRequest.Amount.Satoshi.ToString()
          baseRpcClient.OpenChannelAsync(req, ct).ConfigureAwait false
        match (resp.Error |> Option.ofObj) with
        | None ->
          return () |> Ok
        | Some e ->
          return
            {
              LndOpenChannelError.StatusCode = e.Code |> Option.ofNullable
              Message = resp.Error.Message
            }
            |> Error
      with
      | ex ->
        return
          exnToError ex
          |> Error
    }

    member this.QueryRoutes(nodeId, amount, ?ct) = task {
      // let ct = defaultArg ct CancellationToken.None
      let! resp = baseRpcClient.QueryRoutesAsync(nodeId.ToHex(), amount.Satoshi.ToString()).ConfigureAwait false
      let hops =
        resp.Routes
        |> Seq.head
        |> fun x -> x.Hops
        |> Seq.map(fun (hop: LnrpcHop) -> {
          RouteHop.PubKey = hop.Pub_key |> PubKey
          ShortChannelId = hop.Chan_id |> UInt64.Parse |> ShortChannelId.FromUInt64
          Fee = hop.Fee |> int64 |> LNMoney.MilliSatoshis
          CLTVExpiryDelta =
            hop.Expiry
            |> ValueOption.ofNullable
            |> ValueOption.defaultValue 1L
            |> uint32
        })
        |> List.ofSeq

      return Route hops
    }

    member this.ListChannels(?ct) = task {
      let ct = defaultArg ct CancellationToken.None
      let! c = baseRpcClient.ListChannelsAsync(false, false, false, false, null, ct)
      return
        c.Channels
        |> Seq.map(fun ch -> {
            Id = ch.Chan_id |> UInt64.Parse |> ShortChannelId.FromUInt64
            Cap = ch.Capacity |> Int64.Parse |> Money.Satoshis
            LocalBalance = ch.Local_balance |> Int64.Parse |> Money.Satoshis
            NodeId = ch.Remote_pubkey |> PubKey
          })
        |> Seq.toList
    }

    member this.SubscribeChannelChange(?ct: CancellationToken) = task {
      let ct = defaultArg ct CancellationToken.None
      let l = new WebSocketRawListener<ChannelEventUpdate>(httpClient, "/v1/channels/subscribe", auth, ct)
      return l :> IListener<_>
    }
