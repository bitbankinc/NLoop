module NLoop.CLI.SubCommands.LoopOut

open System
open System.CommandLine
open System.CommandLine.Invocation
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open NLoopClient


let private handle (host: IHost) =
  task {
    let cli = host.Services.GetRequiredService<NLoopClient>()
    let conf = host.Services.GetRequiredService<IConfiguration>()
    let cryptoCode = conf.GetValue<CryptoCode>("cryptocode")
    let req = host.Services.GetRequiredService<IOptions<LoopOutRequest>>().Value
    cli.BaseUrl <- conf.GetValue("url")
    let! resp = cli.OutAsync(cryptoCode, req)
    return resp
  }
let command: Command =
  let command = Command("out", "Perform Reverse submarine swap and get inbound liquidity")
  command.AddAmountOption()
  command.AddChannelOption()
  command.AddLabelOption()
  command.Handler <-
    CommandHandler.Create(Func<IHost,_>(handle))
  command
