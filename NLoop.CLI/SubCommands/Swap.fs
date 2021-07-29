module NLoop.CLI.SubCommands.Swap

open System
open System.CommandLine
open System.CommandLine.Invocation

open FSharp.Control.Tasks.Affine

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection

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


let command: Command =
  let command = Command("swap", "Query swaps")
  command.AddCommand(OnGoing.command)
  command.AddCommand(History.command)
  command
