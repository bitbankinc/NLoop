[<AutoOpen>]
module internal Helpers

open LndClient
open System
open System.Linq
open System.Net.Http
open System.Security.Authentication
open System.Security.Cryptography.X509Certificates

let getHash (cert: X509Certificate2) =
  use alg = System.Security.Cryptography.SHA256.Create()
  alg.ComputeHash(cert.RawData)

let createHttpClient(settings: LndRestSettings, defaultHttpClient: HttpClient option) : HttpClient =
  match defaultHttpClient with
  | Some x when settings.AllowInsecure && settings.Uri.Scheme = "http" ->
    x
  | Some x when settings.MaybeCertificateThumbprint.IsNone && settings.Uri.Scheme = "https" ->
    x
  | _ ->

    let handler = new HttpClientHandler()
    handler.SslProtocols <- SslProtocols.Tls12

    settings.MaybeCertificateThumbprint
    |> Option.iter(fun x ->
      handler.ServerCertificateCustomValidationCallback <-
        let cb = fun _request _cert (chain: X509Chain) _errors ->
          let actualCert = chain.ChainElements.[chain.ChainElements.Count - 1].Certificate
          let hash = getHash(actualCert)
          hash.SequenceEqual(x)
        Func<_,_,_,_,_>(cb)
    )

    if (settings.AllowInsecure) then
      handler.ServerCertificateCustomValidationCallback <-
      let cb = fun _ _ _ _ -> true
      Func<_,_,_,_,_>(cb)
    else if settings.Uri.Scheme = "http" then
      raise <| InvalidOperationException("AllowInsecure is set to false, but the URI is not using https")
    new HttpClient(handler)
