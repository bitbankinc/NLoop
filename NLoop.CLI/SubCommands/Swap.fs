module NLoop.CLI.SubCommands.Swap

open System
open System.CommandLine
open System.CommandLine.Invocation

open System.Threading
open FSharp.Control.Tasks.Affine

open Microsoft.Extensions.Hosting

open NLoopClient
open NLoop.CLI

[<RequireQualifiedAccess>]
module OnGoing =
  let handle (host: IHost)  =
    task {
      let cli = host.Services.GetNLoopClient()
      let! resp = cli.OngoingAsync()
      printfn $"{resp |> Seq.toList}"
    }
  let handler = CommandHandler.Create(Func<IHost,_>(handle))
  let command: Command =
    let command = Command("ongoing", "get list of ongoing swaps")
    command.Handler <- handler
    command

[<RequireQualifiedAccess>]
module History =
  let handle (host: IHost)  =
    task {
      let cli = host.Services.GetNLoopClient()
      let! resp = cli.HistoryAsync()
      let res =
        resp
        |> Newtonsoft.Json.JsonConvert.SerializeObject
      printfn $"{res}"
    }
  let handler = CommandHandler.Create(Func<IHost,_>(handle))
  let command: Command =
    let command = Command("history", "get the whole history of the swaps.")
    command.Handler <- handler
    command


open FSharp.Control
open System.Threading.Tasks

[<RequireQualifiedAccess>]
module Listen =
  let private handle (host: IHost) =
    let a ct =
      asyncSeq {
        let cli = host.Services.GetNLoopClient()
        try
          let ae =
            cli.ListenToEventsAsync(ct)
            |> AsyncSeq.ofAsyncEnum
          printfn "Start Listening"
          do!
            ae
              |> AsyncSeq.iter(fun ev -> printfn $"{ev}")
        with
        | :? TaskCanceledException as _ex ->
          printfn "Cancelled"
          return ()
        | :? ApiException<Response> as ex ->
          let str =
            let errors = ex.Result.Errors |> List.ofSeq
            $"\n{ex.Message}.\nStatusCode: {ex.StatusCode}.\nerrors: {errors}"
          return failwith str
      }
    let cts = new CancellationTokenSource()
    Console.CancelKeyPress.Add(fun _e ->
      cts.Cancel()
    )
    try
      a cts.Token
      |> AsyncSeq.toListAsync
      |> Async.StartAsTask
      :> Task
    with
    | :? TaskCanceledException as _ex ->
      Task.CompletedTask

  let command: Command =
    let command = Command("listen", "Listen to events")
    command.Handler <-
      CommandHandler.Create(Func<IHost,_>(handle))
    command

let command: Command =
  let command = Command("swap", "Query swaps")
  command.AddCommand(OnGoing.command)
  command.AddCommand(History.command)
  command.AddCommand(Listen.command)
  command
