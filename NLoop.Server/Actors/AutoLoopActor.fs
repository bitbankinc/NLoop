namespace NLoop.Server.Actors

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.Utils
open NLoop.Server
open FSharp.Control.Tasks
open FsToolkit.ErrorHandling

type AutoLoopActor(
                   logger: ILogger<SwapActor>,
                   eventAggregator: IEventAggregator,
                   opts: IOptions<NLoopOptions>,
                   clientProvider: ILightningClientProvider
  )  =

  let aggr =
    { AutoLoop.GetSwapParams =
        fun () -> failwith "todo"
      AutoLoop.GetAllChannelIds =
        fun () ->
          clientProvider.GetAllClients()
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
  let handler =
    AutoLoop.getHandler aggr (opts.Value.EventStoreUrl |> Uri)

  member val Handler = handler with get
  member val Aggregate = aggr with get

  member this.Execute(channelId, msg: AutoLoop.Command, ?source) = task {
    logger.LogDebug($"New Command {msg}")
    let source = source |> Option.defaultValue (nameof(AutoLoopActor))
    let cmd =
      { ESCommand.Data = msg
        Meta = { CommandMeta.Source = source
                 EffectiveDate = UnixDateTime.UtcNow } }
    match! handler.Execute channelId cmd with
    | Ok events ->
      events
      |> List.iter(fun e ->
        eventAggregator.Publish e
        eventAggregator.Publish e.Data
        eventAggregator.Publish({ AutoLoop.EventWithId.Id = channelId; AutoLoop.EventWithId.Event = e.Data })
      )
      return Ok events
    | Error s ->
      logger.LogError($"Error when executing {nameof(AutoLoopActor)} %A{s}")
      eventAggregator.Publish({ AutoLoop.ErrorWithId.Id = channelId; AutoLoop.ErrorWithId.Error = s })
      return Error (s.ToString())
  }

