namespace NLoop.Server

open System
open NLoop.Infrastructure.DTOs

module LoopHandlers =
    open Microsoft.AspNetCore.Http
    open FSharp.Control.Tasks
    open Giraffe
    open NLoop.Server.Models

    let handleLoopOut (loopOut: LoopOutRequest) =
        fun (next : HttpFunc) (ctx : HttpContext) ->
            task {
                let response = {
                  LoopOutResponse.Id = (ShortGuid.fromGuid(Guid()))
                }
                return! json response next ctx
            }

    let handleLoopIn (loopIn: LoopInRequest) =
        fun (next : HttpFunc) (ctx : HttpContext) ->
            task {
                let response = {
                    LoopInResponse.Id = (ShortGuid.fromGuid(Guid()))
                }
                return! json response next ctx
            }
