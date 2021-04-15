namespace NLoop.Server

open System.Text.Json
open Giraffe
open Microsoft.AspNetCore.Http
open FSharp.Control.Tasks.Affine

[<AutoOpen>]
module CustomHandlers =
  let bindJsonWithCryptoCode<'T> cryptoCode (f: 'T -> HttpHandler): HttpHandler =
      fun (next : HttpFunc) (ctx : HttpContext) ->
          task {
              let modelR =
                SupportedCryptoCode.Parse cryptoCode
                |> Result.map(fun ourCryptoCode ->
                  let repo = ctx.GetService<IRepositoryProvider>().GetRepository(crypto=ourCryptoCode)
                  JsonSerializer.DeserializeAsync<'T>(ctx.Request.Body, repo.JsonOpts)
                  )
              match modelR with
              | Ok modelT ->
                let! model = modelT
                return! f model next ctx
              | Error e ->
                ctx.SetStatusCode StatusCodes.Status400BadRequest
                return! ctx.WriteJsonAsync({|error = e|})
          }
