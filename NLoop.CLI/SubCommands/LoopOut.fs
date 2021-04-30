module NLoop.CLI.SubCommands.LoopOut

open System
open System.CommandLine
open System.CommandLine.Binding
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open NLoop.Server
open NLoopClient

open NLoop.CLI

let private handle (host: IHost) =
  task {
    let cli = host.Services.GetRequiredService<NLoopClient>()
    let conf = host.Services.GetRequiredService<IConfiguration>()
    let cryptoCode = conf.GetValue<CryptoCode>("cryptocode")
    let opts = host.Services.GetRequiredService<IOptions<NLoopOptions>>().Value
    cli.Configure(opts)
    let pr = host.Services.GetRequiredService<ParseResult>()
    let req =
      let r = LoopOutRequest()
      r.Address <- pr.ValueForOption<string>("address")
      r.Amount <- pr.ValueForOption<int64>("amount")
      r.Channel_id <- pr.ValueForOption<string>("channel")
      r

    let! resp = cli.OutAsync(cryptoCode, req)
    return resp
  }
let command: Command =
  let command = Command("out", "Perform Reverse submarine swap and get inbound liquidity")
  command
    .AddChannelOption()
    .AddCounterPartyPairOption()
    .AddAddressOption()
    .AddAmountOption()
    .AddConfTargetOption()
    .AddLabelOption()
    |> ignore
  command.Handler <-
    CommandHandler.Create(Func<IHost,_>(handle))
  command
