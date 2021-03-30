namespace NLoop.CLI.SubCommands

open System
open System.CommandLine
open System.CommandLine.Invocation
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open NLoopClient
open NLoop.CLI

module LoopIn =
  let handle (host: IHost) =
    task {
      let cli = host.Services.GetRequiredService<NLoopClient>()
      let conf = host.Services.GetRequiredService<IConfiguration>()
      let cryptoCode = conf.GetValue<CryptoCode>("cryptocode")
      let req = host.Services.GetRequiredService<IOptions<LoopInRequest>>().Value
      let pr = host.Services.GetRequiredService<InvocationContext>().ParseResult
      cli.Configure(conf, pr)
      return! cli.InAsync(cryptoCode, req)
    }
  let handler = CommandHandler.Create(Func<IHost,_>(handle))
  let command: Command =
    let command = Command("in", "Perform submarine swap And get outbound liquidity")
    command.AddAmountOption()
    command.AddChannelOption()
    command.AddLabelOption()
    command.Handler <- handler
    command

