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
      let cli = host.Services.GetRequiredService<NLoopClient>()
      let conf = host.Services.GetRequiredService<IConfiguration>()
      let cryptoCode = conf.GetValue<CryptoCode>("cryptocode")
      let opts = host.Services.GetRequiredService<IOptions<NLoop.Server.NLoopOptions>>()
      cli.Configure(opts.Value)
      try
        let req = host.Services.GetRequiredService<IOptions<LoopInRequest>>().Value
        let pr = host.Services.GetRequiredService<ParseResult>()
        req.Pair_id <- pr.ValueForOption<string>("pair_id")
        req.Amount <- pr.ValueForOption<int64>("amount")
        req.Channel_id <-
          pr.ValueForOption<uint64>("channel_id")
          |> ShortChannelId.FromUInt64
          |> fun cid -> cid.ToString()
        return! cli.InAsync(req)
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

