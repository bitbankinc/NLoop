namespace NLoop.CLI

open System
open System.Net.Http
open System.Runtime.InteropServices
open System.Security.Authentication
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Text.Json
open System.Threading
open NBitcoin
open NBitcoin.DataEncoders
open NLoop.Infrastructure.Utils

[<AutoOpen>]
module private Helpers =
  let getHash(cert: X509Certificate2) =
    use hashAlg = SHA256.Create()
    hashAlg.ComputeHash(cert.RawData)

  let createHttpClient(conf: NLoopClientConfig) (defaultHttpClient: HttpClient option): HttpClient =
    match defaultHttpClient with
    | Some h when conf.AllowInsecure && conf.Uri.Scheme = "http" -> h
    | Some h when (conf.CertificateThumbPrint.IsNone && conf.Uri.Scheme = "https") -> h
    | _ ->
      use handler = new HttpClientHandler()
      handler.SslProtocols <- SslProtocols.Tls12
      let expectedThumbprint = conf.CertificateThumbPrint |> Option.map(Seq.toArray)
      expectedThumbprint
      |> Option.iter(fun x ->
        handler.ServerCertificateCustomValidationCallback <-
          fun req cert chain err ->
            let actualCert = chain.ChainElements.[chain.ChainElements.Count - 1].Certificate
            let h = getHash(actualCert)
            Seq.forall2 (=) h x
        )
      if conf.AllowInsecure then
        handler.ServerCertificateCustomValidationCallback <- fun _ _ _ _ -> true
      else if (conf.Uri.Scheme = "http") then
        raise <| InvalidOperationException("AllowInsecure is set to false, but the URI is not using https")
      new HttpClient(handler)

type NLoopClient(conf: NLoopClientConfig,[<O;DefaultParameterValue(null)>]cert: X509Certificate2,
                 [<O;DefaultParameterValue(null)>]httpClient: HttpClient) =
  let httpClient = Option.ofObj httpClient |> createHttpClient conf
  let jsonOpts = JsonSerializerOptions()

  member private this.SendCommandAsync<'TResp>(subPath: string, method: HttpMethod,
                                              parameters: obj, ct: CancellationToken) = async {
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
    use! resp = httpClient.SendAsync(httpReq, ct) |> Async.AwaitTask
    if (not resp.IsSuccessStatusCode) then
      let! errMsg = resp.Content.ReadAsStringAsync(ct) |> Async.AwaitTask
      raise <| HttpRequestException(errMsg)
    let! content = resp.Content.ReadAsStringAsync(ct) |> Async.AwaitTask
    if (String.IsNullOrEmpty(content)) then
      return Unchecked.defaultof<'TResp>
    else
      return JsonSerializer.Deserialize<'TResp>(content, jsonOpts)
  }
  member this.GetVersionAsync([<O;DefaultParameterValue(null)>] ct: CancellationToken) =
    this.SendCommandAsync<string>("/v1/version", HttpMethod.Get, null, ct) |> Async.StartAsTask

  member this.LoopOutAsync() =
    failwith "TODO"
