namespace NLoop.Server

open System.Text.Json
open Giraffe
open Microsoft.AspNetCore.Http
open FSharp.Control.Tasks.Affine
open NLoop.Domain
open NLoop.Server.DTOs

[<AutoOpen>]
module CustomHandlers =
  let bindJsonWithCryptoCode<'T> cryptoCode (f: SupportedCryptoCode -> 'T -> HttpHandler): HttpHandler =
      fun (next : HttpFunc) (ctx : HttpContext) ->
          task {
              let errorResp() =
                ctx.SetStatusCode StatusCodes.Status400BadRequest
                ctx.WriteJsonAsync({|error = $"unsupported cryptocode {cryptoCode}" |})
              match SupportedCryptoCode.TryParse cryptoCode with
              | Some c ->
                match (ctx.GetService<IRepositoryProvider>().TryGetRepository c) with
                | Some repo ->
                  let! model =
                    JsonSerializer.DeserializeAsync<'T>(ctx.Request.Body, repo.JsonOpts)
                  return! f c model next ctx
                | None -> return! errorResp()
              | None -> return! errorResp()
          }

  type SSEEvent = {
    Name: string
    Data: obj
    Id: string
    Retry: int option
  }
