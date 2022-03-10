namespace LndClient

open System
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open LnClientDotnet
open NBitcoin


type NLoopCLightningClient(network: Network, address: Uri) =
  let client = CLightningClient(network, address)
  interface INLoopLightningClient with
    member this.ConnectPeer(nodeId, host, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let! _resp = client.SendCommandAsync<obj>("connect", ct = ct)
        return ()
      } :> Task
    member this.GetChannelInfo(channelId, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        return failwith "todo"
      }
    member this.GetDepositAddress(ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        return failwith "todo"
      }
    member this.GetHodlInvoice(paymentHash, value, expiry, routeHints, memo, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        return failwith "todo"
      }
    member this.GetInfo(ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let! resp = this.SendCommandAsync<CLightningDTOs.GetInfoResponse>("getinfo", ct = ct)
        return resp |> box
      }

    member this.GetInvoice(paymentPreimage, amount, expiry, routeHint, memo, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        return failwith "todo"
      }
    member this.ListChannels(ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let! resp = this.SendCommandAsync<CLightningDTOs.ListChannelsResponse>("listchannels", ct = ct)
        match resp with
        | Error e ->
          return raise <| CLightningRPCException e
        | Ok r ->
          return
            r.Channels
            |> Seq.map(fun r ->
              {
                ListChannelResponse.Cap = r.Satoshis
                Id = r.ShortChannelId
                LocalBalance = failwith "todo"
                RemoteBalance = failwith "todo"
                NodeId = failwith "todo" })
            |> Seq.toList
      }
    member this.Offer(req, ct) =
      failwith "todo"
    member this.OpenChannel(request, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        return failwith "todo"
      }
    member this.QueryRoutes(nodeId, amount, maybeOutgoingChanId, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        return failwith "todo"
      }
    member this.SendPayment(req, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        return failwith "todo"
      }
    member this.SubscribeChannelChange(ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        return failwith "todo"
      }
    member this.SubscribeSingleInvoice(invoiceHash, c) =
      task {
        let ct = defaultArg ct CancellationToken.None
        return failwith "todo"
      }
    member this.TrackPayment(invoiceHash, c) =
      task {
        let ct = defaultArg ct CancellationToken.None
        return failwith "todo"
      }
