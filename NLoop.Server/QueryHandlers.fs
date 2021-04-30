namespace NLoop.Server

open System
open Microsoft.AspNetCore.Http
open Giraffe
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Options
open Microsoft.Extensions.Primitives
open NLoop.Domain
open NLoop.Server.DTOs

module QueryHandlers =

  let handleGetInfo =
    fun (next: HttpFunc) (ctx: HttpContext) ->
      task {
        printfn $"opts: {ctx.GetService<IOptions<NLoopOptions>>().Value.RPCHost}"
        let _logger = ctx.GetLogger("handleGetInfo")
        let response = {
          GetInfoResponse.Version = Constants.AssemblyVersion
          SupportedCoins = { OnChain = [SupportedCryptoCode.BTC; SupportedCryptoCode.LTC]
                             OffChain = [SupportedCryptoCode.BTC] }
        }
        let! r = json response next ctx
        return r
      }

  let handleListenEvent =
    fun (next : HttpFunc) (ctx : HttpContext) ->
      task {
       ctx.Response.Headers.Add("Cache-Control", "no-cache" |> StringValues)
       ctx.Response.Headers.Add("Content-Type", "text/event-stream" |> StringValues)
       do! ctx.Response.Body.FlushAsync()

       let e = ctx.GetService<EventAggregator>()
       use s = e.Subscribe(fun _ -> true)
       while true do
         let! data = e.WaitNext()
         do! ctx.Response.WriteAsJsonAsync({ SSEEvent.Data = data
                                             Id = Guid.NewGuid().ToString()
                                             Name = "TODO"
                                             Retry = None })
         ()
       do! ctx.Response.WriteAsync("\n")
       do! ctx.Response.Body.FlushAsync()
       return! ctx.WriteTextAsync("Event Stream Finished")
      }
