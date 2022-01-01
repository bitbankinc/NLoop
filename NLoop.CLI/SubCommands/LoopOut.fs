module NLoop.CLI.SubCommands.LoopOut

open System
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.Binding
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Server
open NLoopClient

open NLoop.CLI

let private handle (host: IHost) =
  task {
    let cli = host.Services.GetNLoopClient()
    let pr = host.Services.GetRequiredService<ParseResult>()
    let req =
      let r = LoopOutRequest()
      r.Channel_id <- pr.ValueForOption<string[]>("channel")
      r.Pair_id <- pr.ValueForOption<string>("pair_id")
      r.Address <- pr.ValueForOption<string>("address")
      r.Amount <- pr.ValueForOption<int64>("amount")
      r.Swap_tx_conf_requirement <- pr.ValueForOption<int>("swap-tx-conf-requirement")
      r.Label <- pr.ValueForOption<string>("label")
      r

    try
      let! resp = cli.OutAsync(req)
      printfn $"{resp.ToJson()}"
    with
    | :? ApiException<Response> as ex ->
      let str =
        let errors = ex.Result.Errors |> List.ofSeq
        $"\n{ex.Message}.\nStatusCode: {ex.StatusCode}.\nerrors: {errors}"
      return failwith str
  }

let command: Command =
  let command = Command("out", "Perform Reverse submarine swap and get inbound liquidity")
  command
    .AddChannelOption()
    .AddPairIdOption()
    .AddAddressOption()
    .AddAmountOption()
    .AddConfRequirementOption()
    .AddLabelOption()
    |> ignore

  command.Handler <-
    CommandHandler.Create(Func<IHost,_>(handle))
  command
