namespace LndClient

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils.Primitives
open DotNetLightning.ClnRpc
open DotNetLightning.ClnRpc.Requests
open DotNetLightning.Payment
open DotNetLightning.Utils
open FSharp.Control
open Microsoft.Extensions.Logging
open NBitcoin
open NBitcoin.DataEncoders

[<AutoOpen>]
module private CLightningHelpers =
  let hex = HexEncoder()

type NLoopCLightningClient(uri: Uri, network: Network, logger: ILogger<NLoopCLightningClient>) =
  let cli =  ClnClient(network, uri)

  interface INLoopLightningClient with
    member this.ConnectPeer(_nodeId, _host, _ct) =
      backgroundTask {
        raise <| NotSupportedException()
      } :> Task

    member this.GetChannelInfo(channelId, ct): Task<GetChannelInfoResponse option> =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None

        // we need to call `listpeers` rpc to get the information we want,
        // but since the response might be huge, we first call `listchannels` to get the peer id.
        // so that by specifying peer id as an argument for `listpeers`, we can get a much smaller response.
        let! listChannelResp =
          let req = {
            ListchannelsRequest.ShortChannelId = Some <| ShortChannelId.ParseUnsafe(channelId.ToString())
            Source = None
            Destination = None
          }
          cli.ListChannelsAsync(req, ct = ct)

        if listChannelResp.Channels.Length = 0 then return None else
        assert (listChannelResp.Channels.Length = 1)
        let listChannelChannel = listChannelResp.Channels |> Seq.head
        let! peers =
          let req = {
            ListpeersRequest.Id = listChannelChannel.Destination |> Some
            Level = None
          }
          cli.ListPeersAsync(req, ct = ct)

        if peers.Peers.Length = 0 then return None else
        assert (peers.Peers.Length = 1)

        let c =
          peers.Peers
          |> Seq.head
          |> fun p ->
            p.Channels
            |> Seq.pick(fun c ->
              c.ChannelId |> Option.bind(fun cId -> if cId.ToString() = channelId.ToString() then Some c else None)
            )

        let convertNodePolicy (channel: Responses.ListpeersPeersChannels) (node1: bool) = {
          NodePolicy.Disabled = listChannelChannel.Active |> not
          Id =
            if node1 then listChannelChannel.Source
            else listChannelChannel.Destination
          TimeLockDelta =
            // cltv_expiry_delta must be `u16` according to the [bolt07](https://github.com/lightning/bolts/blob/master/07-routing-gossip.md)
            channel.OurToSelfDelay
            |> Option.defaultWith(fun _ -> failwith "bogus listpeers. channel has no our_to_self_delay")
            |> uint16 |> BlockHeightOffset16
          MinHTLC =
            channel.MinimumHtlcInMsat |> Option.defaultValue 0L<msat> |> int64 |> LNMoney.MilliSatoshis
          FeeBase =
            channel.FeeBaseMsat
            |> Option.defaultValue 0L<msat>
            |> int64
            |> LNMoney.MilliSatoshis
          FeeProportionalMillionths =  channel.FeeProportionalMillionths |> Option.defaultValue 0u
        }
        return
          Some <| {
            GetChannelInfoResponse.Capacity =
              c.TotalMsat
              |> Option.defaultValue(0L<msat>)
              |> int64
              |> fun c -> LNMoney.MilliSatoshis(c).ToMoney()
            Node1Policy =
              convertNodePolicy c true
            Node2Policy =
              convertNodePolicy c false
          }
      }
    member this.GetDepositAddress(ct) =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None
        let! resp =
          let req = {
            NewaddrRequest.Addresstype = None
          }
          cli.NewAddrAsync(req, ct = ct)
        match resp.Bech32 with
        | Some b -> return BitcoinAddress.Create(b, network)
        | None -> return failwith "No Address in response."
      }

    member this.GetHodlInvoice(_paymentHash, _value, _expiry, _routeHints, _memo, _ct) =
      backgroundTask {
        return raise <| NotImplementedException()
      }

    member this.GetInfo(ct) =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None
        let! resp = cli.GetinfoAsync(ct = ct)
        return resp |> box
      }
    member this.GetInvoice(paymentPreimage, amount, expiry, routeHint, memo, ct) =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None
        let! resp =
          let req = {
            InvoiceRequest.Preimage = paymentPreimage.ToHex() |> Some
            Msatoshi = AmountOrAny.Amount (amount.Satoshi |> unbox)
            Description = memo
            Label = memo
            Expiry = expiry.TotalSeconds |> uint64 |> Some
            Fallbacks = None
            Exposeprivatechannels = (routeHint |> Seq.isEmpty) |> Some
            Cltv = None
            Deschashonly = true |> Some }
          cli.InvoiceAsync(req, ct = ct)
        return resp.Bolt11 |> PaymentRequest.Parse |> ResultUtils.Result.deref
      }
    member this.ListChannels(ct) =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None
        let! peers =
          let req = {
            ListpeersRequest.Id = None
            Level = None
          }
          cli.ListPeersAsync(req, ct = ct)
        return
          [
            for p in peers.Peers do
              for c in p.Channels do
                if c.State <> Responses.ListpeersPeersChannelsState.CHANNELD_NORMAL then
                  ()
                else
                  let toUs =
                    match c.ToUsMsat with
                    | Some ms -> int64 ms |> LNMoney.MilliSatoshis
                    | None -> failwith "no to_us_msat in response"
                  yield
                    {
                      ListChannelResponse.Id =
                        match c.ShortChannelId with
                        | Some cid -> ShortChannelId.ParseUnsafe <| cid.ToString()
                        | None -> failwith "no channel id in response."
                      Cap =
                        match c.TotalMsat with
                        | Some ms -> LNMoney.MilliSatoshis(int64 ms).ToMoney()
                        | None -> failwith "no total_msat in response."
                      LocalBalance =
                        let ourReserve =
                          match c.OurReserveMsat with
                          | Some ms -> LNMoney.MilliSatoshis(int64 ms)
                          | None -> failwith "no our_reserve_msat in response"
                        if ourReserve < toUs then
                          (toUs - ourReserve).ToMoney()
                        else
                          Money.Zero
                      RemoteBalance =
                        let inbound =
                          match c.TotalMsat with
                          | Some ms ->
                            LNMoney.Satoshis(int64 ms) - toUs
                          | None -> failwith "no total_msat in response"
                        let theirReserve =
                          match c.TheirReserveMsat with
                          | Some ms -> LNMoney.Satoshis(int64 ms)
                          | None -> failwith "no their_reserve_msat"
                        if  theirReserve < inbound then
                          (inbound - theirReserve).ToMoney()
                        else
                          inbound.ToMoney()
                      NodeId = p.Id
                    }
          ]
      }
    member this.Offer(req, ct) =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None

        let! resp =
          let r = {
            PayRequest.Bolt11 = req.Invoice.ToString()
            Msatoshi = None
            Label =
              match req.Invoice.Description with
              | Choice1Of2 i -> i |> Some
              | _ -> None
            Riskfactor = None
            Maxfeepercent = None
            RetryFor = None
            Maxdelay = None
            Exemptfee = None
            Localofferid = None
            Exclude = None
            Maxfee = None
            Description = None
          }
          cli.PayAsync(r, ct = ct)
        if resp.Status = Responses.PayStatus.COMPLETE then
          return Ok()
        else
          return Error($"Failed to pay status: {resp.Status}")
      }

    member this.QueryRoutes(nodeId, amount, _maybeOutgoingChanId, ct) =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None
        let! resp =
          let req = {
            GetrouteRequest.Id = nodeId
            Msatoshi = amount.MilliSatoshi |> unbox
            Riskfactor = 1UL
            Cltv = None
            Fromid = None
            Fuzzpercent = None
            Exclude = None
            Maxhops = None
          }
          cli.GetRouteAsync(req, ct)
        let hop =
          resp.Route
          |> Seq.map(fun r -> {
            RouteHop.Fee = r.AmountMsat |> int64 |> LNMoney.MilliSatoshis
            PubKey = r.Id
            ShortChannelId = r.Channel
            CLTVExpiryDelta = r.Delay
          })
          |> Seq.toList
        return Route.Route(hop)
      }
    member this.SendPayment(req, ct) =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None
        if req.Invoice.AmountValue.IsNone then return raise <| InvalidDataException $"Invoice has no amount specified" else
        let maxFeePercent = req.Invoice.AmountValue.Value.Satoshi / req.MaxFee.Satoshi
        let! resp =
          let r  = {
            PayRequest.Bolt11 = req.Invoice.ToString()
            Msatoshi = req.Invoice.AmountValue.Value.MilliSatoshi |> unbox
            Label = None
            Riskfactor = None
            Maxfeepercent = Some maxFeePercent
            RetryFor = None
            Maxdelay = None
            Exemptfee = None
            Localofferid = None
            Exclude = None
            Maxfee = (req.MaxFee.Satoshi * 1000L) |> unbox
            Description = None
          }
          cli.PayAsync(r, ct)
        if resp.Status = Responses.PayStatus.COMPLETE then
          return Ok {
            PaymentResult.Fee = (resp.AmountSentMsat - resp.AmountMsat) |> int64 |> LNMoney.MilliSatoshis
            PaymentPreimage = resp.PaymentPreimage.ToString() |> hex.DecodeData |> PaymentPreimage.Create
          }
        else
          return Error $"Failed SendPay ({resp})"
      }
    member this.SubscribeSingleInvoice({ Label = label }, ct) =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None
        let! resp =
          let req = {
            WaitinvoiceRequest.Label = label
          }
          cli.WaitInvoiceAsync(req, ct)

        return
          {
            PaymentRequest = resp.Bolt11.ToString() |> PaymentRequest.Parse |> ResultUtils.Result.deref
            InvoiceState =
              if resp.Status = Responses.WaitinvoiceStatus.PAID then
                IncomingInvoiceStateUnion.Settled
              else if resp.Status = Responses.WaitinvoiceStatus.EXPIRED then
                IncomingInvoiceStateUnion.Canceled
              else
                IncomingInvoiceStateUnion.Unknown
            AmountPayed =
              if resp.Status = Responses.WaitinvoiceStatus.PAID then
                match resp.AmountReceivedMsat with
                | Some s -> s |> int64 |> LNMoney.MilliSatoshis
                | None -> failwith "unreachable! invoice is paied but amount is unknown"
              else
                LNMoney.Zero
          }
      } |> Async.AwaitTask  |> List.singleton |> AsyncSeq.ofSeqAsync

    member this.TrackPayment(invoiceHash, ct) =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None
        let! resp =
          let req = {
            WaitsendpayRequest.PaymentHash = invoiceHash.Value
            Timeout = None
            Partid = None
            Groupid = None
          }
          cli.WaitSendPayAsync(req, ct)

        let translateStatus s =
          match s with
          | Responses.WaitsendpayStatus.COMPLETE -> OutgoingInvoiceStateUnion.Succeeded
          | _ -> OutgoingInvoiceStateUnion.Unknown

        let amountDelivered =
          match resp.AmountMsat with
          | Some s -> s
          | None ->
            let msg = "amount_msat is not included in waitsendpay response. This might end up showing fee lower than expected"
            logger.LogWarning msg
            resp.AmountSentMsat
        return {
          OutgoingInvoiceSubscription.InvoiceState = resp.Status |> translateStatus
          Fee = (resp.AmountSentMsat - amountDelivered) |> int64 |> LNMoney.MilliSatoshis
          PaymentRequest =
            match resp.Bolt11 with
            | Some bolt11 ->
              bolt11 |> PaymentRequest.Parse |> ResultUtils.Result.deref
            | None -> failwith "bolt11 not found in waitsendpay response"
          AmountPayed = amountDelivered |> int64 |> LNMoney.MilliSatoshis
        }
      } |> Async.AwaitTask |> List.singleton |> AsyncSeq.ofSeqAsync

