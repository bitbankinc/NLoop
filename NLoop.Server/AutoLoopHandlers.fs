module NLoop.Server.AutoLoopHandlers

open System
open System.Threading.Tasks
open FSharp.Control.Tasks
open Microsoft.AspNetCore.Http
open Giraffe
open Giraffe.Core
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server.RPCDTOs
open FsToolkit.ErrorHandling

let handleSetRule (req: SetRuleRequest): HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) -> task {
    let handler =
      let aggr =
        { AutoLoop.GetSwapParams =
            fun () -> failwith "todo"
          AutoLoop.GetAllChannels =
            fun () ->
              ctx.GetService<ILightningClientProvider>().GetAllClients()
              |> Seq.map(fun c -> c.ListChannels())
              |> Task.WhenAll
              |> Task.map(List.concat)
              |> Task.map(List.map(fun t -> {
                AutoLoop.Data.Id = t.Id
                Cap =  t.Cap
                LocalBalance = t.LocalBalance
                NodeId = t.NodeId
              }))
        }
        |> AutoLoop.getAggregate
      let opts = ctx.GetService<IOptions<NLoopOptions>>()
      AutoLoop.getHandler aggr (opts.Value.EventStoreUrl |> Uri)
    let cmd =
      { ESCommand.Data =
          AutoLoop.Command.SetRule(req.AsDomain)
        Meta = { CommandMeta.Source = "handleSetRule"
                 EffectiveDate = UnixDateTime.UtcNow }
      }
    match! handler.Execute () cmd with
    | Ok events ->
      ctx.GetService<IEventAggregator>().Publish(events)
      return! next ctx
    | Error e ->
      return! error503 e next ctx
  }
