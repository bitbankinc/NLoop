namespace LndClient

open System
open System.Collections.Generic
open System.Linq
open System.IO
open System.Net.Http
open System.Runtime.CompilerServices
open System.Security.Cryptography.X509Certificates
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils
open DotNetLightning.Utils.Primitives
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
open NBitcoin.RPC
open NBitcoin.RPC
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

[<AbstractClass;Sealed;Extension>]
type GrpcClientExtensions =

  [<Extension>]
  static member ApplyLndSettings(this: GrpcChannelOptions, settings: LndGrpcSettings) =
    this.HttpHandler <- settings.CreateHttpClientHandler()
    match settings.Macaroon with
    | Some _ when settings.Url.Scheme <> "https" ->
      failwith $"The grpc url must be https when using certificate. It was {settings.Url.Scheme}"
    | Some _ ->
      let maybeMacaroonHex =
        settings.Macaroon
        |> Option.map(
          function
          | MacaroonInfo.Raw macaroon ->
            macaroon.SerializeToBytes() |> Encoders.Hex.EncodeData
          | MacaroonInfo.FilePath filePath ->
            if not <| filePath.EndsWith(".macaroon", StringComparison.OrdinalIgnoreCase) then
              raise <| ArgumentException($"filePath ({filePath}) is not a macaroon file", nameof(filePath))
            else
              filePath |> File.ReadAllBytes |> Encoders.Hex.EncodeData
        )
      this.Credentials <-
        let callCred = CallCredentials.FromInterceptor(fun ctx metadata -> unitTask {
            try
              maybeMacaroonHex
              |> Option.iter(fun macaroon ->
                metadata.Add("macaroon", macaroon)
              )
            with
            | ex -> Console.WriteLine $"Unreachable! CallCredentials Interceptor failed {ex}"
        })
        ChannelCredentials.Create(SslCredentials(), callCred)
    | None ->
      ()

[<Extension;AbstractClass;Sealed>]
type GrpcTypeExt =
  [<Extension>]
  static member ToOutPoint(a: ChannelPoint) =
    let o = OutPoint()
    o.Hash <-
      if a.FundingTxidCase = ChannelPoint.FundingTxidOneofCase.FundingTxidBytes then
        a.FundingTxidBytes.ToByteArray() |> uint256
      elif a.FundingTxidCase = ChannelPoint.FundingTxidOneofCase.FundingTxidStr then
        a.FundingTxidStr |> uint256.Parse
      else
        assert(a.FundingTxidCase = ChannelPoint.FundingTxidOneofCase.None)
        null
    o.N <- a.OutputIndex
    o
  [<Extension>]
  static member ToOutPoint(a: PendingUpdate) =
    let o = OutPoint()
    o.Hash <-
      a.Txid.ToByteArray() |> uint256
    o.N <-
      a.OutputIndex
    o


[<AutoOpen>]
module Extensions =
  type ListChannelResponse
    with
    static member FromGrpcType(o: Channel) =
      {
        ListChannelResponse.Id = o.ChanId |> ShortChannelId.FromUInt64
        Cap = o.Capacity |> Money.Satoshis
        LocalBalance = o.LocalBalance |> Money.Satoshis
        NodeId = o.RemotePubkey |> PubKey
        RemoteBalance = o.RemoteBalance |> Money.Satoshis
      }
  type LndClient.HopHint with
    member h.ToGrpcType() =
      let lnHopHint = HopHint()
      lnHopHint.NodeId <- h.NodeId.Value.ToHex()
      lnHopHint.ChanId <- h.ShortChannelId.ToUInt64()
      lnHopHint.CltvExpiryDelta <- h.CLTVExpiryDelta.Value |> uint32
      lnHopHint.FeeBaseMsat <- h.FeeBase.MilliSatoshi |> uint32
      lnHopHint.FeeProportionalMillionths <- h.FeeProportionalMillionths |> uint
      lnHopHint

  type LndClient.ChannelEventUpdate
    with
    static member FromGrpcType(r: ChannelEventUpdate) =
      match r.Type with
      | ChannelEventUpdate.Types.UpdateType.ActiveChannel ->
        r.ActiveChannel.ToOutPoint()
        |> LndClient.ChannelEventUpdate.ActiveChannel
      | ChannelEventUpdate.Types.UpdateType.InactiveChannel ->
        r.InactiveChannel.ToOutPoint()
        |> ChannelEventUpdate.InActiveChannel
      | ChannelEventUpdate.Types.UpdateType.OpenChannel ->
        r.OpenChannel
        |> ListChannelResponse.FromGrpcType
        |> OpenChannel
      | ChannelEventUpdate.Types.UpdateType.PendingOpenChannel ->
        r.PendingOpenChannel.ToOutPoint()
        |> PendingOpenChannel
      | ChannelEventUpdate.Types.UpdateType.ClosedChannel ->
        let c = r.ClosedChannel
        {|
          Id = c.ChanId |> ShortChannelId.FromUInt64
          CloseTxHeight = c.CloseHeight |> BlockHeight
          TxId = c.ClosingTxHash |> hex.DecodeData |> uint256
        |}
        |> ClosedChannel
      | ChannelEventUpdate.Types.UpdateType.FullyResolvedChannel ->
        r.FullyResolvedChannel.ToOutPoint()
        |> FullyResolvedChannel
      | x -> failwith $"Unreachable! Unknown type {x}"


/// grpc-dotnet does not support specifying custom ssl credential which is necessary in case of using LND securely.
/// ref: https://github.com/grpc/grpc/issues/21554
/// So we must set custom HttpMessageHandler for HttpClient which performs validation.
type NLoopLndGrpcClient(settings: LndGrpcSettings, network: Network) =
  let channel =
    let opts = GrpcChannelOptions()
    opts.ApplyLndSettings(settings)
    GrpcChannel.ForAddress(settings.Url, opts)
  let client =
    Lightning.LightningClient(channel)
  let invoiceClient = Invoices.InvoicesClient(channel)
  let routerClient = Router.RouterClient(channel)

  member val Client = client with get

  member this.DefaultHeaders = null
    //let metadata = Metadata()
    // metadata.Add()
    // metadata

  member this.Deadline =
    Nullable(DateTime.UtcNow + TimeSpan.FromSeconds(10.))

  member this.ConnectPeer(nodeId: PubKey, host, ct) =
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
  member this.ConnectPeer(nodeId: PubKey, host, ct) = this.ConnectPeer(nodeId, host, Some ct)
  member this.GetDepositAddress(ct) =
    task {
      let ct = defaultArg ct CancellationToken.None
      let req = NewAddressRequest()
      let! m = client.NewAddressAsync(req, this.DefaultHeaders, this.Deadline, ct).ResponseAsync
      return BitcoinAddress.Create(m.Address, network)
    }

  member this.GetDepositAddress(ct) = this.GetDepositAddress(Some ct)
  member this.GetHodlInvoice(paymentHash: Primitives.PaymentHash, value: LNMoney, ?expiry: TimeSpan, ?routeHints, ?memo, ?ct) =
    task {
      let ct = defaultArg ct CancellationToken.None
      let req = AddHoldInvoiceRequest()
      req.Hash <-
        paymentHash.ToBytes()
        |> ByteString.CopyFrom
      req.Value <- value.Satoshi
      expiry |> Option.iter(fun e ->
        req.Expiry <- e.Seconds |> int64
      )
      routeHints |> Option.iter(fun routeHintsValue ->
        for r in routeHintsValue do
          let lnRouteHint = RouteHint()
          lnRouteHint.HopHints.AddRange(r.Hops |> Array.map(fun h -> h.ToGrpcType()))
          req.RouteHints.Add(lnRouteHint)
      )
      match memo with
      | Some m ->
        req.Memo <- m
      | None ->
        req.Memo <- "hodl_invoice_requested_by_LndGrpcClient"
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

  member this.SubscribeSingleInvoice({ Hash = invoiceHash }, ct) =
    let ct = defaultArg ct CancellationToken.None
    let resp =
      let req = SubscribeSingleInvoiceRequest()
      req.RHash <- invoiceHash.ToBytes() |> ByteString.CopyFrom
      // we need to set longer deadline for grpc itself too.
      // since we don't know how long it will take, we just set null.
      let deadline = Nullable()
      invoiceClient.SubscribeSingleInvoice(req, this.DefaultHeaders, deadline, ct).ResponseStream
    let translateEnum status =
      match status with
      | Invoice.Types.InvoiceState.Open -> IncomingInvoiceStateUnion.Open
      | Invoice.Types.InvoiceState.Accepted -> IncomingInvoiceStateUnion.Accepted
      | Invoice.Types.InvoiceState.Canceled -> IncomingInvoiceStateUnion.Canceled
      | Invoice.Types.InvoiceState.Settled -> IncomingInvoiceStateUnion.Settled
      | _ -> IncomingInvoiceStateUnion.Unknown

    resp.ReadAllAsync(ct)
    |> AsyncSeq.ofAsyncEnum
    |> AsyncSeq.map(fun inv ->
      { IncomingInvoiceSubscription.InvoiceState = inv.State |> translateEnum
        IncomingInvoiceSubscription.PaymentRequest = inv.PaymentRequest |> PaymentRequest.Parse |> ResultUtils.Result.deref
        IncomingInvoiceSubscription.AmountPayed = inv.ValueMsat |> LNMoney.MilliSatoshis
      }
    )
  member this.SubscribeSingleInvoice(req, ct) =
    this.SubscribeSingleInvoice(req, Some ct)

  member this.TrackPayment(invoiceHash: Primitives.PaymentHash, ct) =
    let ct = defaultArg ct CancellationToken.None
    let resp =
      let req = TrackPaymentRequest()
      req.PaymentHash <- invoiceHash.ToBytes() |> ByteString.CopyFrom
      // we need to set longer deadline for grpc itself too.
      // since we don't know how long it will take, we just set null.
      let deadline = Nullable()
      routerClient.TrackPaymentV2(req, this.DefaultHeaders, deadline, ct).ResponseStream
    let translateEnum status =
      match status with
      | Payment.Types.PaymentStatus.InFlight ->  OutgoingInvoiceStateUnion.InFlight
      | Payment.Types.PaymentStatus.Failed -> OutgoingInvoiceStateUnion.Failed
      | Payment.Types.PaymentStatus.Succeeded -> OutgoingInvoiceStateUnion.Succeeded
      | Payment.Types.PaymentStatus.Unknown
      | _ -> OutgoingInvoiceStateUnion.Unknown

    resp.ReadAllAsync(ct)
    |> AsyncSeq.ofAsyncEnum
    |> AsyncSeq.map(fun inv ->
      { OutgoingInvoiceSubscription.InvoiceState = inv.Status |> translateEnum
        Fee = inv.FeeMsat |> LNMoney.MilliSatoshis
        PaymentRequest = inv.PaymentRequest |> PaymentRequest.Parse |> ResultUtils.Result.deref
        AmountPayed = inv.ValueMsat |> LNMoney.MilliSatoshis
      }
    )

  member this.GetInvoice(amount: LNMoney, ?paymentPreimage: PaymentPreimage, ?expiry: TimeSpan, ?routeHints, ?memo, ?ct) =
    task {
      let req = Invoice()
      let ct = defaultArg ct CancellationToken.None
      paymentPreimage |> Option.iter( fun img ->
        req.RPreimage <-
          img.ToByteArray() |> ByteString.CopyFrom
      )
      req.Value <- amount.Satoshi
      expiry |> Option.iter(fun e ->
        req.Expiry <- e.TotalSeconds |> int64
      )
      routeHints |> Option.iter(fun routeHintsValue ->
        for r in routeHintsValue do
          let lnRouteHint = RouteHint()
          lnRouteHint.HopHints.AddRange(r.Hops |> Array.map(fun h -> h.ToGrpcType()))
          req.RouteHints.Add(lnRouteHint)
      )
      match memo with
      | Some m ->
        req.Memo <- m
      | None ->
        req.Memo <- "invoice_requested_by_LndGrpcClient"
      let! r = client.AddInvoiceAsync(req, this.DefaultHeaders, this.Deadline, ct)
      return r.PaymentRequest |> PaymentRequest.Parse |> ResultUtils.Result.deref
    }
  member this.GetInvoice(paymentPreimage: PaymentPreimage, amount: LNMoney, expiry: TimeSpan, routeHints, memo, ct) =
    this.GetInvoice(amount, paymentPreimage, expiry, routeHints, memo, ct)
  member this.ListChannels(ct) =
    task {
      let ct = defaultArg ct CancellationToken.None
      let req = ListChannelsRequest()
      let! r = client.ListChannelsAsync(req, this.DefaultHeaders, this.Deadline, ct)
      return
        r.Channels
        |> Seq.map(fun c ->
          let pendingSum = c.PendingHtlcs |> Seq.sumBy(fun p -> p.Amount) |> Money.Satoshis
          let c = ListChannelResponse.FromGrpcType c
          { c
            with
              LocalBalance = c.LocalBalance - pendingSum
              RemoteBalance = c.RemoteBalance - pendingSum
          }
        )
        |> Seq.toList
    }
  member this.ListChannels(ct) = this.ListChannels(Some ct)

  member this.SendPayment(param, ct) =
    let ct = defaultArg ct CancellationToken.None
    task {
      let responseStream =
        let req = SendPaymentRequest()
        req.PaymentRequest <- param.Invoice.ToString()
        req.OutgoingChanIds.AddRange(param.OutgoingChannelIds |> Seq.map(fun c -> c.ToUInt64()))
        req.FeeLimitSat <- param.MaxFee.Satoshi
        req.MaxParts <-
          let count = param.OutgoingChannelIds.Count()
          if count = 0 then 1u else count |> uint
        req.TimeoutSeconds <-
          param.TimeoutSeconds
        routerClient.SendPaymentV2(req, this.DefaultHeaders, this.Deadline, ct).ResponseStream

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
              result <- $"payment failed. reason: {status.FailureReason}" |> Error |> Some
            | Payment.Types.PaymentStatus.InFlight ->
              ()
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
  member this.SendPayment(param, ct) = this.SendPayment(param, Some ct)

  member this.Offer(param, ct) = task {
    let ct = defaultArg ct CancellationToken.None
    let responseStream =
      let req = SendPaymentRequest()
      req.PaymentRequest <- param.Invoice.ToString()
      req.OutgoingChanIds.AddRange(param.OutgoingChannelIds |> Seq.map(fun c -> c.ToUInt64()))
      req.FeeLimitSat <- param.MaxFee.Satoshi
      req.MaxParts <-
        let count = param.OutgoingChannelIds.Count()
        if count = 0 then 1u else count |> uint
      req.TimeoutSeconds <-
        param.TimeoutSeconds
      // we need to set longer deadline for grpc itself too.
      let deadline = Nullable(DateTime.UtcNow + TimeSpan.FromSeconds(float param.TimeoutSeconds + 10.))
      routerClient.SendPaymentV2(req, this.DefaultHeaders, deadline, ct).ResponseStream
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
          | Payment.Types.PaymentStatus.Succeeded
          | Payment.Types.PaymentStatus.InFlight ->
            result <- Some(Ok())
            ()
          | Payment.Types.PaymentStatus.Failed ->
            result <- $"payment failed. reason: {status.FailureReason}" |> Error |> Some
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
  member this.Offer(param, ct) = this.Offer(param, Some ct)
  member this.OpenChannel(request, ct) =
    task {
      let ct = defaultArg ct CancellationToken.None
      let req = OpenChannelRequest()
      req.Private <- request.Private |> Option.defaultValue true
      request.CloseAddress |> Option.iter(fun addr ->
        req.CloseAddress <- addr
      )
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
  member this.OpenChannel(request, ct) = this.OpenChannel(request, Some ct)
  member this.QueryRoutes(nodeId: PubKey, amount: LNMoney, maybeOutgoingChanId: ShortChannelId option, ct) =
    task {
      let ct = defaultArg ct CancellationToken.None
      let req = QueryRoutesRequest()
      req.PubKey <- nodeId.ToHex()
      req.Amt <- amount.Satoshi
      maybeOutgoingChanId
      |> Option.iter(fun chanId ->
        req.OutgoingChanId <- chanId.ToUInt64()
      )
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
  member this.GetChannelInfo(channelId: ShortChannelId, ct: CancellationToken option) = task {
    let ct = defaultArg ct CancellationToken.None
    let! resp =
      let req = ChanInfoRequest()
      req.ChanId <- channelId.ToUInt64()
      client.GetChanInfoAsync(req, this.DefaultHeaders, this.Deadline, ct).ResponseAsync

    if resp |> isNull then return None else
    let convertNodePolicy (nodeIdStr: string) (p: RoutingPolicy) = {
      NodePolicy.Disabled = p.Disabled
      Id = nodeIdStr |> PubKey
      TimeLockDelta =
        // cltv_expiry_delta must be `u16` according to the [bolt07](https://github.com/lightning/bolts/blob/master/07-routing-gossip.md)
        p.TimeLockDelta |> uint16 |> BlockHeightOffset16
      MinHTLC = p.MinHtlc |> LNMoney.MilliSatoshis
      FeeBase = p.FeeBaseMsat |> LNMoney.MilliSatoshis
      FeeProportionalMillionths = p.FeeRateMilliMsat |> uint32
    }
    return {
      GetChannelInfoResponse.Capacity = resp.Capacity |> Money.Satoshis
      Node1Policy = resp.Node1Policy |> convertNodePolicy resp.Node1Pub
      Node2Policy = resp.Node2Policy |> convertNodePolicy resp.Node2Pub
    } |> Some
  }

  member this.SettleInvoice(preimage: PaymentPreimage, ct) =
    task {
      let ct = defaultArg ct CancellationToken.None
      let! _ =
        let msg = SettleInvoiceMsg()
        msg.Preimage <- ByteString.CopyFrom(preimage.ToByteArray())
        invoiceClient.SettleInvoiceAsync(msg, this.DefaultHeaders, this.Deadline, ct)
      ()
    } :> Task


  member this.SendCoins(dest: BitcoinAddress, amt: Money, targetConf: BlockHeightOffset32, ct) =
    task {
      let! resp =
        let req = SendCoinsRequest()
        req.Addr <- dest.ToString()
        req.Amount <- amt.Satoshi
        req.TargetConf <- targetConf.Value |> int
        client.SendCoinsAsync(req, this.DefaultHeaders, this.Deadline, ct)
      return resp.Txid |> uint256.Parse
    }
  member this.GetDepositAddress(network, ct) =
    task {
      let! resp =
        let req = NewAddressRequest()
        req.Type <- Lnrpc.AddressType.WitnessPubkeyHash
        client.NewAddressAsync(req, this.DefaultHeaders, this.Deadline, ct)
      return BitcoinAddress.Create(resp.Address, network)
    }

  interface IWalletClient with
    member this.GetDepositAddress(network, ct) =
      let ct = defaultArg ct CancellationToken.None
      this.GetDepositAddress(network, ct)

    member this.FundToAddress(dest: BitcoinAddress, amt: Money, targetConf, ?ct) =
      let ct = defaultArg ct CancellationToken.None
      this.SendCoins(dest, amt, targetConf, ct)

    member this.GetSendingTxFee(destinations, target, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        try
          let! resp =
            let r = EstimateFeeRequest()
            r.TargetConf <- target.Value |> int
            let d = destinations.ToDictionary((fun kv -> kv.Key.ToString()), (fun kv -> kv.Value.Satoshi))
            r.AddrToAmount.Add(d)
            client.EstimateFeeAsync(r, this.DefaultHeaders, this.Deadline, ct)
          return resp.FeeSat |> Money.Satoshis |> Ok
        with
        | ex when ex.Message.Contains("insufficient funds", StringComparison.OrdinalIgnoreCase) ->
          return Error(WalletClientError.InSufficientFunds(ex.ToString()))
      }

  interface INLoopLightningClient with
    member this.ConnectPeer(nodeId, host, ct) =
      try
        this.ConnectPeer(nodeId, host, ct)
      with
      | :? RpcException as e ->
        raise <| NLoopLightningClientException(NLoopLightningClientError.Lnd e)
    member this.GetDepositAddress(ct) =
      try
        this.GetDepositAddress(ct)
      with
      | :? RpcException as e ->
        raise <| NLoopLightningClientException(NLoopLightningClientError.Lnd e)
    member this.GetHodlInvoice(paymentHash, value, expiry, routeHints, memo, ct) =
      let ct = defaultArg ct CancellationToken.None

      try
        this.GetHodlInvoice(paymentHash, value, expiry, routeHints, memo, ct)
      with
      | :? RpcException as e ->
        raise <| NLoopLightningClientException(NLoopLightningClientError.Lnd e)
    member this.GetInfo(ct) =
      try
        this.GetInfo ct
      with
      | :? RpcException as e ->
        raise <| NLoopLightningClientException(NLoopLightningClientError.Lnd e)
    member this.SubscribeSingleInvoice(invoiceHash, ct) =
      try
        this.SubscribeSingleInvoice(invoiceHash, ct)
      with
      | :? RpcException as e ->
        raise <| NLoopLightningClientException(NLoopLightningClientError.Lnd e)
    member this.TrackPayment(invoiceHash, ct) =
      try
        this.TrackPayment(invoiceHash, ct)
      with
      | :? RpcException as e ->
        raise <| NLoopLightningClientException(NLoopLightningClientError.Lnd e)
    member this.GetInvoice(paymentPreimage, amount, expiry, routeHints, memo, ct) =
      let ct = defaultArg ct CancellationToken.None
      try
        this.GetInvoice(paymentPreimage, amount, expiry, routeHints, memo, ct)
      with
      | :? RpcException as e ->
        raise <| NLoopLightningClientException(NLoopLightningClientError.Lnd e)
    member this.ListChannels(ct) =
      try
        this.ListChannels ct
      with
      | :? RpcException as e ->
        raise <| NLoopLightningClientException(NLoopLightningClientError.Lnd e)
    member this.SendPayment(param, ct) =
      try
        this.SendPayment (param, ct)
      with
      | :? RpcException as e ->
        raise <| NLoopLightningClientException(NLoopLightningClientError.Lnd e)
    member this.Offer(param, ct) =
      try
        this.Offer(param, ct)
      with
      | :? RpcException as e ->
        raise <| NLoopLightningClientException(NLoopLightningClientError.Lnd e)
    member this.QueryRoutes(nodeId, amount, maybeOutgoingChanId, ct) =
      try
        this.QueryRoutes(nodeId, amount, maybeOutgoingChanId, ct)
      with
      | :? RpcException as e ->
        raise <| NLoopLightningClientException(NLoopLightningClientError.Lnd e)
    member this.GetChannelInfo(channelId: ShortChannelId, ?ct: CancellationToken) =
      try
        this.GetChannelInfo(channelId, ct)
      with
      | :? RpcException as e ->
        raise <| NLoopLightningClientException(NLoopLightningClientError.Lnd e)
