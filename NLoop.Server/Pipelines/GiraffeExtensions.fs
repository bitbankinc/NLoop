namespace NLoop.Server.Pipelines

open System
open FsToolkit.ErrorHandling
open Giraffe
open Microsoft.AspNetCore.Http
open FSharp.Control.Tasks.Affine
open NLoop.Server.Handlers

[<AutoOpen>]
module CustomHandlers =
  type HttpContext with

    member this.TryGetDate(arg: string) =
      this.TryGetQueryStringValue arg
      |> Option.map(fun s ->
        match DateTime.TryParse s with
        | true, r -> (Ok r)
        | _ -> Error $"Invalid datetime format ({s}) for parameter {arg}"
      )

  let inline internal error503 e =
    setStatusCode StatusCodes.Status503ServiceUnavailable
      >=> json {| error = e.ToString() |}

  let inline internal error500 e =
    setStatusCode StatusCodes.Status500InternalServerError
      >=> json {| error = e.ToString() |}
  let inline internal errorBadRequest (errors: #seq<string>) =
    setStatusCode StatusCodes.Status400BadRequest
      >=> json {| errors = errors |}

  let internal handleHandlerError =
    fun (next: HttpFunc) (ctx: HttpContext) (r: Result<_, HandlerError>) -> task {
      match r with
      | Ok ok -> return! json ok next ctx
      | Error(HandlerError.InvalidRequest e) ->
        return! errorBadRequest e next ctx
      | Error(HandlerError.InternalError e) ->
        return! error500 e next ctx
      | Error(HandlerError.ServiceUnAvailable e) ->
        return! error503 e next ctx
    }
  
