namespace LndClient

open System
open System.Net.Http
open DotNetLightning.Payment
open DotNetLightning.Utils
open FSharp.Control.Tasks
open LndSwaggerClient.Base
open LndSwaggerClient.Invoice
open NBitcoin
open NBitcoin.DataEncoders

type LndNSwagClient(network: Network, restSettings: LndRestSettings, ?defaultHttpClient: HttpClient) =
  let httpClient = createHttpClient(restSettings, defaultHttpClient)
  let url =
      let b = UriBuilder(restSettings.Uri)
      b.UserName <- ""
      b.Password <- ""
      b.Uri.ToString()
  do
    let auth = restSettings.CreateLndAuth()
    httpClient.AddLndAuthentication(auth)
  let invoiceRpcClient = LndSwaggerInvoiceClient(url, httpClient)
  let baseRpcClient = LndSwaggerBaseClient(url, httpClient)

  member val InvoiceRpcClient = invoiceRpcClient with get
  member val BaseRpcClient = baseRpcClient with get

  member private this.GetPaymentPreimage(paymentHash: string) = task {
    let! p = baseRpcClient.ListPaymentsAsync().ConfigureAwait false
    return
      p.Payments
      |> Seq.filter(fun i -> i.Payment_hash = paymentHash)
      |> Seq.tryExactlyOne
      |> Option.map(fun i -> i.Payment_preimage)
  }

  interface INLoopLightningClient with
    member this.ConnectPeer(nodeId, host) = unitTask {
      let req = LnrpcConnectPeerRequest()
      req.Addr <-
        let addr = LnrpcLightningAddress()
        addr.Host <- host
        addr.Pubkey <- nodeId.ToHex()
        addr
      let! _ = baseRpcClient.ConnectPeerAsync(req).ConfigureAwait false
      return ()
    }
    member this.GetDepositAddress() = task {
      let! a = baseRpcClient.NewAddressAsync()
      return BitcoinAddress.Create(a.Address, network)
    }
    member this.GetHodlInvoice(paymentHash, value, expiry, memo) = task {
      let! resp =
        let req = InvoicesrpcAddHoldInvoiceRequest()
        req.Hash <- paymentHash.ToBytes()
        req.Value_msat <- value.MilliSatoshi.ToString()
        req.Expiry <- expiry.Seconds.ToString()
        req.Memo <- memo
        invoiceRpcClient.AddHoldInvoiceAsync(req).ConfigureAwait false
      return
        resp.Payment_request
        |> PaymentRequest.Parse
        |> ResultUtils.Result.deref
    }

    member this.GetInfo() = task {
      let! r = baseRpcClient.GetInfoAsync().ConfigureAwait false
      return r |> box
    }

    member this.GetInvoice(paymentPreimage, amount, expiry, memo) = task {
      let! resp =
        let req = LndSwaggerClient.Base.LnrpcInvoice()
        req.R_preimage <- paymentPreimage.ToByteArray()
        req.Value_msat <- amount.MilliSatoshi.ToString()
        req.Expiry <- expiry.Seconds.ToString()
        req.Memo <- memo
        baseRpcClient.AddInvoiceAsync(req).ConfigureAwait false
      return
        resp.Payment_request
        |> PaymentRequest.Parse
        |> ResultUtils.Result.deref
    }
    member this.Listen() = task {
      return failwith "todo"
    }

    member this.Offer(invoice) = task {
      let! resp =
        let req = LnrpcSendRequest()
        req.Payment_request <- invoice.ToString()
        baseRpcClient.SendPaymentSyncAsync(req).ConfigureAwait false
      if resp.Payment_error |> String.IsNullOrEmpty && resp.Payment_preimage |> isNull |> not then
        return
          resp.Payment_preimage
          |> PaymentPreimage.Create
          |> Ok
      elif resp.Payment_error = "invoice is already paid" then
        let! maybePreimage = this.GetPaymentPreimage(invoice.PaymentHash.Value.ToString())
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
          let req = LnrpcOpenChannelRequest()
          req.Private <- openChannelRequest.Private |> Option.toNullable
          req.Close_address <- openChannelRequest.CloseAddress |> Option.toObj
          req.Node_pubkey_string <- openChannelRequest.NodeId.ToHex()
          req.Node_pubkey <- openChannelRequest.NodeId.ToBytes()
          req.Local_funding_amount <- openChannelRequest.Amount.Satoshi.ToString()
          baseRpcClient.OpenChannelAsync(req).ConfigureAwait false
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

    member this.QueryRoutes(nodeId, amount) = task {
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

    member this.ListChannels() = task {
      let! c = baseRpcClient.ListChannelsAsync()
      return
        c.Channels
        |> Seq.map(fun ch -> {
            Id = ch.Chan_id |> UInt64.Parse |> ShortChannelId.FromUInt64
            Cap = ch.Capacity |> Int64.Parse |> Money.Satoshis
            LocalBalance = ch.Local_balance |> Int64.Parse |> Money.Satoshis
          })
        |> Seq.toList
    }
