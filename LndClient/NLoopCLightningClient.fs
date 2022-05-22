namespace LndClient

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open DotNetLightning.ClnRpc
open DotNetLightning.ClnRpc.Requests
open DotNetLightning.Payment
open DotNetLightning.Utils
open NBitcoin
open NBitcoin.DataEncoders

[<AutoOpen>]
module private CLightningHelpers =
  let hex = HexEncoder()

type NLoopCLightningClient(uri: Uri, network: Network) =
  let cli =  ClnClient(uri)

  new(uri: Uri) = NLoopCLightningClient(uri, Network.RegTest)

  interface INLoopLightningClient with
    member this.ConnectPeer(nodeId, host, ct) =
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
            ListchannelsRequest.ShortChannelId = Some <| ShortChannelId(channelId.ToString())
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

        let p, c =
          peers.Peers
          |> Seq.head
          |> fun p ->
            p.Channels
            |> Seq.pick(fun c ->
              c.ChannelId |> Option.bind(fun cId -> if cId.ToString() = channelId.ToString() then Some (p, c) else None)
            )

        let convertNodePolicy (p: ListpeersPeers) (channel: ListpeersPeersChannels) (node1: bool) = {
          NodePolicy.Disabled = listChannelChannel.Active |> not
          Id =
            if node1 then listChannelChannel.Source.Value |> PubKey
            else listChannelChannel.Destination.Value |> PubKey
          TimeLockDelta =
            // cltv_expiry_delta must be `u16` according to the [bolt07](https://github.com/lightning/bolts/blob/master/07-routing-gossip.md)
            channel.OurToSelfDelay
            |> Option.defaultWith(fun _ -> failwith "bogus listpeers. channel has no our_to_self_delay")
            |> uint16 |> BlockHeightOffset16
          MinHTLC =
            channel.MinimumHtlcInMsat |> Option.defaultValue 0L<msat> |> LNMoney.MilliSatoshis
          FeeBase =
            channel.FeeBaseMsat
            |> Option.defaultValue 0L<msat>
            |> LNMoney.MilliSatoshis
          FeeProportionalMillionths =  channel.FeeProportionalMillionths |> Option.defaultValue 0u
        }
        return
          Some <| {
            GetChannelInfoResponse.Capacity =
              c.TotalMsat
              |> Option.defaultValue(0L<msat>)
              |> fun c -> LNMoney.MilliSatoshis(c).ToMoney()
            Node1Policy =
              convertNodePolicy p c true
            Node2Policy =
              convertNodePolicy p c false
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

    member this.GetHodlInvoice(paymentHash, value, expiry, routeHints, memo, ct) =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None
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
                if c.State <> ListpeersPeersChannelsState.CHANNELD_NORMAL then
                  ()
                else
                  let toUs =
                    match c.ToUsMsat with
                    | Some ms -> LNMoney.MilliSatoshis(ms)
                    | None -> failwith "no to_us_msat in response"
                  yield
                    {
                      ListChannelResponse.Id =
                        match c.ShortChannelId with
                        | Some cid -> ShortChannelId.ParseUnsafe <| cid.ToString()
                        | None -> failwith "no channel id in response."
                      Cap =
                        match c.TotalMsat with
                        | Some ms -> LNMoney.MilliSatoshis(ms).ToMoney()
                        | None -> failwith "no total_msat in response."
                      LocalBalance =
                        let ourReserve =
                          match c.OurReserveMsat with
                          | Some ms -> LNMoney.MilliSatoshis(ms)
                          | None -> failwith "no our_reserve_msat in response"
                        if ourReserve < toUs then
                          (toUs - ourReserve).ToMoney()
                        else
                          Money.Zero
                      RemoteBalance =
                        let inbound =
                          match c.TotalMsat with
                          | Some ms ->
                            LNMoney.Satoshis(ms) - toUs
                          | None -> failwith "no total_msat in response"
                        let theirReserve =
                          match c.TheirReserveMsat with
                          | Some ms -> LNMoney.Satoshis ms
                          | None -> failwith "no their_reserve_msat"
                        if  theirReserve < inbound then
                          (inbound - theirReserve).ToMoney()
                        else
                          inbound.ToMoney()
                      NodeId = p.Id.Value |> PubKey
                    }
          ]
      }
    member this.Offer(req, ct) =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None

        let! t =
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
        return Ok()
      }

    member this.OpenChannel(request, ct) =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None
        let! resp =
          let reqP: obj array = [|
            request.NodeId.ToHex()
            request.Amount.Satoshi
            null // feerate
            not (request.Private |> Option.defaultValue true) // announce
            null // minconf
            null // utxos
            null // push_msat
            request.CloseAddress // close_to
          |]
          cli.SendCommandAsync<CLightningDTOs.fundchannel.Fundchannel>("fundchannel", reqP, cancellation = ct)

        let outpoint = OutPoint()
        outpoint.Hash <- resp.Txid.ToString() |> uint256
        outpoint.N <- resp.Outnum.ToString() |> UInt32.Parse
        return Ok outpoint
      }

    member this.QueryRoutes(nodeId, amount, maybeOutgoingChanId, ct) =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None
        let! resp =
          let req: obj array  = [|
            nodeId.ToHex();
            amount.MilliSatoshi
            1 // risk factor
          |]
          cli.SendCommandAsync<CLightningDTOs.getroute.Route[]>("getroute", req, cancellation = ct)
        let hop =
          resp
          |> Seq.map(fun r -> {
            RouteHop.Fee = r.Amount_msat.AsLNMoney()
            PubKey = r.Id.AsPubKey()
            ShortChannelId = r.Channel.ToString() |> ShortChannelId.ParseUnsafe
            CLTVExpiryDelta = r.Delay.ToString() |> uint32
          })
          |> Seq.toList
        return Route.Route(hop)
      }
    member this.SendPayment(req, ct) =
      backgroundTask {
        let ct = defaultArg ct CancellationToken.None
        if req.Invoice.AmountValue.IsNone then return raise <| InvalidDataException $"Invoice has no amount specified" else
        let maxFeePercent = req.Invoice.AmountValue.Value / req.MaxFee
        let! resp =
          cli.SendCommandAsync<CLightningDTOs.pay.Pay>("pay", [| req.Invoice.ToString() |], cancellation = ct)
        if resp.Status = CLightningDTOs.pay.PayStatus.Complete then
          return Ok {
            PaymentResult.Fee = resp.Amount_sent_msat.AsLNMoney() - resp.Amount_msat.AsLNMoney()
            PaymentPreimage = resp.Payment_preimage.ToString() |> hex.DecodeData |> PaymentPreimage.Create
          }
        else
          return Error $"Failed SendPay ({resp})"
      }
    member this.SubscribeChannelChange(ct) =
      backgroundTask {
        return failwith "todo"
      }
    member this.SubscribeSingleInvoice(invoiceHash, c) =
      backgroundTask {
        return failwith "todo"
      }
    member this.TrackPayment(invoiceHash, c) =
      backgroundTask {
        return failwith "todo"
      }

