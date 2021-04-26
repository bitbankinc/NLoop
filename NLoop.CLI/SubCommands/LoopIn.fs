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
      let opts = host.Services.GetRequiredService<IOptions<NLoop.Server.NLoopOptions>>()
      cli.Configure(opts.Value)
      return! cli.InAsync(cryptoCode, req)
    }
  let handler = CommandHandler.Create(Func<IHost,_>(handle))
  let command: Command =
    let command = Command("in", "Perform submarine swap And get outbound liquidity")
    command
      .AddAmountOption()
      .AddChannelOption()
      .AddCounterPartyPairOption()
      .AddCryptoCodeOption()
      .AddLabelOption()
      |> ignore
    command.Handler <- handler
    command

