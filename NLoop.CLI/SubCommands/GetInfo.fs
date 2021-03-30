namespace NLoop.CLI.SubCommands

open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection

open NLoop.CLI
open Microsoft.Extensions.Hosting
open NLoopClient

module GetInfo =
  let handle (host: IHost)  =
    task {
      let cli = host.Services.GetRequiredService<NLoopClient>()
      let conf = host.Services.GetRequiredService<IConfiguration>()
      let pr = host.Services.GetRequiredService<InvocationContext>().ParseResult
      cli.Configure(conf, pr)
      let! resp = cli.InfoAsync()
      printfn $"{resp.ToJson()}"
    }
  let handler = CommandHandler.Create(Func<IHost,_>(handle))
  let command: Command =
    let command = Command("getinfo", "get general info")
    command.Handler <- handler
    command

