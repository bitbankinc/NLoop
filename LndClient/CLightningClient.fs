namespace LndClient

open System
open System.Linq
open System.Buffers
open System.IO
open System.Net
open System.Net.Sockets
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open NBitcoin
open NLoop.Domain.IO
open NLoop.Domain.Utils

type CLightningClientErrorCodeEnum =
  // -- errors from `pay`, `sendpay` or `waitsendpay` commands --
  | IN_PROGRESS = 200
  | RHASH_ALREADY_USED = 201
  | UNPARSABLE_ONION = 202
  | DESTINATION_PERM_FAIL = 203
  | TRY_OTHER_ROUTE = 204
  | ROUTE_NOT_FOUND = 205
  | ROUTE_TOO_EXPENSIVE = 206
  | INVOICE_EXPIRED = 207
  | NO_SUCH_PAYMENT = 208
  | UNSPECIFIED_ERROR = 209
  | STOPPED_RETRYING = 210
  | PAY_STATUS_UNEXPECTED = 211
  | PAY_OFFER_INVALID = 212

  // -- `fundchannel` or `withdraw` errors --
  | MAX_EXCEEDED = 300
  | CANNOT_AFFORD = 301
  | FUND_OUTPUT_IS_DUST = 302
  | FUNDING_BROADCAST_FAIL = 303
  | FUNDING_STILL_SYNCING_BITCOIN = 304
  | FUNDING_PEER_NOT_CONNECTED = 305
  | FUNDING_UNKNOWN_PEER = 306
  | FUNDING_NOTHING_TO_CANCEL = 307
  | FUNDING_CANCEL_NOT_SAFE = 308
  | FUNDING_PSBT_INVALID = 309
  | FUNDING_V2_NOT_SUPPORTED = 310
  | FUNDING_UNKNOWN_CHANNEL = 311
  | FUNDING_STATE_INVALID = 312

  // -- `connect` errors --
  | CONNECT_NO_KNOWN_ADDRESS = 400
  | CONNECT_ALL_ADDRESSES_FAILED = 401

  // -- Errors from `invoice` or `delinvoice` commands
  | INVOICE_LABEL_ALREADY_EXISTS = 900
  | INVOICE_PREIMAGE_ALREADY_EXISTS = 901
  | INVOICE_HINTS_GAVE_NO_ROUTES = 902
  | INVOICE_EXPIRED_DURING_WAIT = 903
  | INVOICE_WAIT_TIMED_OUT = 904
  | INVOICE_NOT_FOUND = 905
  | INVOICE_STATUS_UNEXPECTED = 906
  | INVOICE_OFFER_INACTIVE = 907

  // -- Errors from HSM crypto operations. --
  | HSM_ECDH_FAILED = 800

  // -- Errors from `offer` commands --
  | OFFER_ALREADY_EXISTS = 1000
  | OFFER_ALREADY_DISABLED = 1001
  | OFFER_EXPIRED = 1002
  | OFFER_ROUTE_NOT_FOUND = 1003
  | OFFER_BAD_INREQ_REPLY = 1004
  | OFFER_TIMEOUT = 1005

  // -- Errors from datastore command --
  | DATASTORE_DEL_DOES_NOT_EXIST = 1200
  | DATASTORE_DEL_WRONG_GENERATION = 1201
  | DATASTORE_UPDATE_ALREADY_EXISTS = 1201
  | DATASTORE_UPDATE_DOES_NOT_EXIST = 1203
  | DATASTORE_UPDATE_WRONG_GENERATION = 1204
  | DATASTORE_UPDATE_HAS_CHILDREN = 1205
  | DATASTORE_UPDATE_NO_CHILDREN = 1206

  // -- Errors from `wait` commands --
  | WAIT_TIMEOUT = 2000

[<Struct>]
type CLightningClientErrorCode =
  | Known of CLightningClientErrorCodeEnum
  | Unknown of code: int
  with
  static member FromInt(i: int) =
    if Enum.IsDefined(typeof<CLightningClientErrorCodeEnum>, i) then
      Known (LanguagePrimitives.EnumOfValue i)
    else
      Unknown i

[<Struct>]
type CLightningRPCError = {
  Code: CLightningClientErrorCode
  Msg: string
}

exception CLightningRPCException of CLightningRPCError
module CLightningDTOs =

  type GetInfoAddress = {
    Type: string
    Address: string
    Port: int
  }

  type GetInfoResponse = {
    Address: GetInfoAddress[]
    Id: string
    Version: string
    BlockHeight: int
    Network: string
  }

  type ListChannelsResponseContent = {
    Source: PubKey
    Destination: PubKey
    ShortChannelId: ShortChannelId
    [<JsonPropertyName "public">]
    IsPublic: bool

    Satoshis: Money
    MessageFlags: uint8
    ChannelFlags: uint8
    Active: bool
    LastUpdate: uint // unit time
    BaseFeeMilliSatoshi: int64
    FeePerMillionth: uint32
    Delay: int
    Features: string
  }

  type ListChannelsResponse = {
    Channels: ListChannelsResponseContent[]
  }

type O = OptionalArgumentAttribute
type D = DefaultParameterValueAttribute

type CLightningClient (network: Network, address: Uri) =
  let getAddr (domain: string) =
    task {
      match IPAddress.TryParse domain with
      | true, addr ->
        return addr
      | false, _ ->
        let! a = Dns.GetHostAddressesAsync(domain).ConfigureAwait false
        return a |> Array.tryHead |> Option.defaultWith (fun () -> failwith "Host not found")
    }
  member private this.Connect() =
    task {
      let! socket, endpoint =
        if address.Scheme = "tcp" then
          task {
            let domain = address.DnsSafeHost
            let! addr = getAddr domain
            let socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            let endpoint = IPEndPoint(addr, address.Port) :> EndPoint
            return socket, endpoint
          }
        else if address.Scheme = "unix" then
          task {
            let mutable path = address.AbsoluteUri.Remove(0, "unix:".Length)
            if not <| path.StartsWith "/" then
              path <- $"/{path}"
            while path.Length >= 2 && (path.[0] <> '/' || path.[1] = '/') do
              path <- path.Remove(0, 1)
            if path.Length < 2 then
              raise <| FormatException "Invalid unix url"
            return
              (new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP), UnixDomainSocketEndPoint(path) :> EndPoint)
          }
        else
          raise <| NotSupportedException $"Protocol {address.Scheme} for clightning not supported"
      do! socket.ConnectAsync(endpoint)
      return socket
    }

  member private this.SendCommandAsync<'T>(cmd: string,
                                           [<O;D(null)>]parameters: obj[],
                                           [<O;D(false)>] noReturn: bool,
                                           [<O;D(false)>] isArray: bool,
                                           [<O;D(CancellationToken())>] ct: CancellationToken): Task<Result<'T, CLightningRPCError>> =
    let parameters = if parameters |> isNull then [||] else parameters
    backgroundTask {
      use! socket = this.Connect()
      use networkStream = new NetworkStream(socket)
      use jsonWriter = new Utf8JsonWriter(networkStream)
      jsonWriter.WriteNumber("id", 0)
      jsonWriter.WriteString("method", cmd)
      jsonWriter.WriteStartArray()
      for p in parameters do
        jsonWriter.WriteStringValue (p.ToString())
      jsonWriter.WriteEndArray()
      do! jsonWriter.FlushAsync(ct)
      do! networkStream.FlushAsync(ct)
      let result =
        let opts = JsonSerializerOptions()
        opts.AddNLoopJsonConverters(network)
        JsonSerializer.DeserializeAsync<JsonElement>(networkStream, opts, ct)

      use _ = ct.Register(fun () -> socket.Dispose())
      try
        let! result = result
        match result.TryGetProperty("error") with
        | true, err  ->
          let code = err.GetProperty("code").GetInt32() |> CLightningClientErrorCode.FromInt
          let msg = err.GetProperty("message").GetString()
          return Error { Code = code; Msg = msg  }
        | false, _ ->
          if noReturn then
            return Ok Unchecked.defaultof<'T>
          else
            let jObj =
              if isArray then
                result.GetProperty("result").EnumerateArray().First()
              else
                result.GetProperty("result")
            return Ok <| jObj.Deserialize()
      with
      | _ when ct.IsCancellationRequested ->
        ct.ThrowIfCancellationRequested()
        return failwith "unreachable"
    }


  interface INLoopLightningClient with
    member this.ConnectPeer(nodeId, host, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let! _resp = this.SendCommandAsync<obj>("connect", ct = ct)
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
