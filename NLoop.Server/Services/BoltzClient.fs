namespace NLoop.Server.Services

open System
open System.IO
open System.Net.Http
open System.Net.Http.Json
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks
open System.Threading
open FSharp.Control.Tasks
open Macaroons
open NBitcoin
open System.Security.Cryptography.X509Certificates
open NLoop.Infrastructure

type O = OptionalArgumentAttribute
type D = DefaultParameterValueAttribute

type BoltzClient(address: Uri, network: Network, [<O;D(null)>]cert: X509Certificate2, [<O;D(null)>]macaroon: Macaroon,
                 [<O;D(null)>]httpClient: HttpClient) =
  let httpClient = Option.ofObj httpClient |> Option.defaultValue (new HttpClient())
  let jsonOpts = JsonSerializerOptions()
  do
    jsonOpts.AddNLoopJsonConverters(network.NetworkType)
    jsonOpts.Converters.Add(JsonFSharpConverter())
    jsonOpts.PropertyNameCaseInsensitive <- true

    if (isNull address) then raise <| ArgumentNullException(nameof(address)) else
    if (isNull network) then raise <| ArgumentNullException(nameof(network)) else
    httpClient.BaseAddress <- address
  new (host: string, port, network, [<O;D(null)>] cert, [<O;D(null)>] macaroon, [<O;D(null)>] httpClient) =
    BoltzClient(Uri($"%s{host}:%i{port}"), network, cert, macaroon, httpClient)
  new (host: string, network, [<O;D(null)>] cert, [<O;D(null)>] macaroon, [<O;D(null)>] httpClient) =
    BoltzClient(host, 443, network, cert, macaroon, httpClient)
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

  member this.GetPairs([<O;D(null)>] ct: CancellationToken): Task<GetPairsResponse> =
    this.SendCommandAsync<_>("getpairs", HttpMethod.Get, null, ct)

  member this.GetNodes([<O;D(null)>] ct: CancellationToken) =
    this.SendCommandAsync<GetNodesResponse>("getnodes", HttpMethod.Get, null, ct)

  member this.GetSwapTransaction(id: string, [<O;D(null)>] ct: CancellationToken) =
    this.SendCommandAsync<GetSwapTxResponse>("getswaptransaction", HttpMethod.Post, {| Id = id |}, ct)

  member this.PostSwapStatus(id: string, [<O;D(null)>] ct: CancellationToken) =
    this.SendCommandAsync<SwapStatusResponse>("swapstatus", HttpMethod.Post, {| Id = id|}, ct)

  member this.CreateSwap(req: CreateSwapRequest, [<O;D(null)>] ct: CancellationToken) =
    this.SendCommandAsync<CreateSwapResponse>("createswap", HttpMethod.Post, req, ct)
