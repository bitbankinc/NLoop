namespace NLoop.CLI.SubCommands

open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open DotNetLightning.Utils.Primitives
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
      let cli = host.Services.GetNLoopClient()
      let req = host.Services.GetRequiredService<IOptions<LoopInRequest>>().Value
      let pr = host.Services.GetRequiredService<ParseResult>()
      req.Pair_id <- pr.ValueForOption<string>("pair_id")
      req.Amount <- pr.ValueForOption<int64>("amount")
      req.Channel_id <-
        pr.ValueForOption<uint64>("channel_id")
        |> ShortChannelId.FromUInt64
        |> fun cid -> cid.ToString()
      req.Label <-
        pr.ValueForOption<string>("label")
      try
        let! resp = cli.InAsync(req)
        printfn $"{resp.ToJson()}"
      with
      | :? ApiException<Response> as ex ->
        let str =
          let errors = ex.Result.Errors |> List.ofSeq
          $"\n{ex.Message}.\nStatusCode: {ex.StatusCode}.\nerrors: {errors}"
        return failwith str
    }
  let handler = CommandHandler.Create(Func<IHost,_>(handle))
  let command: Command =
    let command = Command("in", "Perform submarine swap And get outbound liquidity")
    command
      .AddAmountOption()
      .AddChannelOption()
      .AddPairIdOption()
      .AddLabelOption()
      |> ignore
    command.Handler <- handler
    command

