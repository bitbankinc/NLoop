namespace NLoop.Server

open System.Runtime.CompilerServices
open System.Text.Json
open System.Text.Json.Serialization
open Giraffe
open Microsoft.AspNetCore.Http
open Giraffe
open Giraffe.Core
open FSharp.Control.Tasks.Affine

[<Extension;AbstractClass;Sealed>]
type GiraffeExtensions() =
  [<Extension>]
  static member BindJsonWithCryptoCodeAsync(this: HttpContext, cryptoCode: string) = task {
    let repo = this.GetService<RepositoryProvider>().GetRepository(cryptoCode)
    return! JsonSerializer.DeserializeAsync(this.Request.Body, repo.JsonOpts)
  }



[<AutoOpen>]
module CustomHandlers =
  let bindJsonWithCryptoCode<'T> cryptoCode (f: 'T -> HttpHandler): HttpHandler =
      fun (next : HttpFunc) (ctx : HttpContext) ->
          task {
              let! model = ctx.BindJsonWithCryptoCodeAsync<'T>(cryptoCode)
              return! f model next ctx
          }
