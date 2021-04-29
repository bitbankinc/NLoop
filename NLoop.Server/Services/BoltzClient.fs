namespace NLoop.Server.Services

open System
open FSharp.Control
open System.Collections.Concurrent
open System.IO
open System.Threading.Channels
open System.Net.Http
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Threading.Tasks
open System.Threading
open DotNetLightning.Payment
open FSharp.Control.Tasks
open Microsoft.Extensions.Options
open NBitcoin
open System.Security.Cryptography.X509Certificates
open NLoop.Server
open NLoop.Domain.IO
open NLoop.Domain

type D = DefaultParameterValueAttribute

type SwapStatusUpdate = {
  NewStatus: SwapStatusResponse
  Id: string
}

exception BoltzRPCException of string

type BoltzClient([<O;D(null)>]httpClient: HttpClient) =
  let httpClient = Option.ofObj httpClient |> Option.defaultValue (new HttpClient())
  let jsonOpts = JsonSerializerOptions()
  do
    jsonOpts.AddNLoopJsonConverters(Network.RegTest)
    jsonOpts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase

  new (uri: Uri) =
    let h = new HttpClient()
    h.BaseAddress <- uri
    BoltzClient(h)

  new (host: string, ?port: int) =
    let port = port |> Option.defaultValue(if host.StartsWith("https") then 443 else 80)
    BoltzClient(Uri($"{host}:{port}"))

  member val HttpClient = httpClient with get
  with

  member private this.SendCommandAsync<'TResp>(subPath: string, method: HttpMethod,
                                               parameters: obj, ct: CancellationToken) = task {
    use httpReq =
      let m = new HttpRequestMessage()
      m.Method <- method
      m.RequestUri <- Uri(httpClient.BaseAddress, subPath)
      m

    do
      if parameters <> null then
        let content = JsonSerializer.Serialize(parameters, jsonOpts)
        httpReq.Content <- new StringContent(content, Encoding.UTF8, "application/json")
      else
        ()

    use! resp = httpClient.SendAsync(httpReq, ct)
    if (not resp.IsSuccessStatusCode) then
      let! errMsg = resp.Content.ReadAsStringAsync(ct)
      raise <| HttpRequestException(errMsg)

    let! content = resp.Content.ReadAsStringAsync(ct)
    if (String.IsNullOrEmpty(content)) then
      return Unchecked.defaultof<'TResp>
    else
      return JsonSerializer.Deserialize(content, jsonOpts)
  }

  member this.GetVersionAsync([<O;D(null)>] ct: CancellationToken): Task<GetVersionResponse> =
    this.SendCommandAsync<_>("version", HttpMethod.Get, null, ct)

  member this.GetPairsAsync([<O;D(null)>] ct: CancellationToken): Task<GetPairsResponse> =
    this.SendCommandAsync<_>("getpairs", HttpMethod.Get, null, ct)

  member this.GetNodesAsync([<O;D(null)>] ct: CancellationToken) =
    this.SendCommandAsync<GetNodesResponse>("getnodes", HttpMethod.Get, null, ct)

  member this.GetFeeEstimation([<O;D(null)>] ct: CancellationToken) =
    this.SendCommandAsync<Map<string, int64>>("getfeeestimation", HttpMethod.Get, null, ct)

  member this.GetTransactionAsync(currency: SupportedCryptoCode, txId: uint256, [<O;D(null)>] ct: CancellationToken) =
    this.SendCommandAsync<GetTxResponse>("gettransaction", HttpMethod.Post, {| transactionId = txId; Currency = currency.ToString() |}, ct)

  member this.GetSwapTransactionAsync(id: string, [<O;D(null)>] ct: CancellationToken) =
    this.SendCommandAsync<GetSwapTxResponse>("getswaptransaction", HttpMethod.Post, {| Id = id |}, ct)

  member this.GetSwapStatusAsync(id: string, [<O;D(null)>] ct: CancellationToken) =
    this.SendCommandAsync<SwapStatusResponse>("swapstatus", HttpMethod.Post, {| Id = id |}, ct)

  member this.CreateSwapAsync(req: CreateSwapRequest, [<O;D(null)>]channel: ChannelOpenRequest, [<O;D(null)>] ct: CancellationToken) =
    let reqObj = {| req with Type = "submarine" |}
    let reqObj = if channel |> box |> isNull then reqObj |> box else {| reqObj with Channel = channel |} |> box
    this.SendCommandAsync<CreateSwapResponse>("createswap", HttpMethod.Post, reqObj, ct)

  member this.CreateReverseSwapAsync(req: CreateReverseSwapRequest, [<O;D(null)>] ct: CancellationToken) =
    let reqObj = {| req with Type = "reversesubmarine" |}
    this.SendCommandAsync<CreateReverseSwapResponse>("createswap", HttpMethod.Post, reqObj, ct)

  member this.CreateChannelCreation(req: CreateChannelRequest, [<O;D(null)>] ct: CancellationToken) =
    let reqObj = {| req with Type = "submarine" |}
    this.SendCommandAsync<CreateSwapResponse>("createswap", HttpMethod.Post, reqObj, ct)

  member this.GetSwapRatesAsync(swapId: string, [<O;D(null)>] ct: CancellationToken) =
    this.SendCommandAsync<GetSwapRatesResponse>("swaprates", HttpMethod.Post, {| Id = swapId |}, ct)

  member this.SetInvoiceAsync(swapId: string, invoice: PaymentRequest, [<O;D(null)>] ct: CancellationToken) =
    this.SendCommandAsync<SetInvoiceResponse option>("setinvoice", HttpMethod.Post, {| Id = swapId; Invoice = invoice.ToString() |}, ct)

  member this.StartListenToSwapStatusChange(id, [<O;D(null)>] ct: CancellationToken) =
    asyncSeq {
      let! x = httpClient.GetStreamAsync($"/streamswapstatus?id=%s{id}") |> Async.AwaitTask
      use streamReader = new StreamReader(x)
      while not <| streamReader.EndOfStream && not <| ct.IsCancellationRequested do
        let! msg = streamReader.ReadLineAsync() |> Async.AwaitTask
        yield JsonSerializer.Deserialize<SwapStatusResponse>(msg, jsonOpts)
    }

