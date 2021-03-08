namespace NLoop.CLI

open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open NBitcoin
open NLoop.Infrastructure.DTOs
open NLoop.Server.Services

module private CommandLineValidators =
  let networkValidator = ValidateSymbol<_>(fun (r: CommandResult) ->
    let hasNetwork = r.Children.Contains("network")
    let hasMainnet = r.Children.Contains("mainnet")
    let hasTestnet = r.Children.Contains("testnet")
    let hasRegtest = r.Children.Contains("regtest")
    let mutable count = 0
    for flag in seq [hasNetwork; hasMainnet; hasTestnet; hasRegtest] do
      if (flag) then
        count <- count + 1
    if (count > 1) then "You cannot specify more than one network" else
    null
    )


module NLoopCLICommandLine =
  let getOptions: Option seq =
    seq [
      let networkNames = Network.GetNetworks() |> Seq.map(fun n -> n.Name) |> Array.ofSeq
      let o = System.CommandLine.Option<string>([| "-n"; "--network" |], $"Set the network from ({String.Join(',', networkNames)}) (default: mainnet)")
      o.Argument <-
        let a = Argument<string>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a.FromAmong(networkNames)
      o

      Option<bool>([|"--mainnet"|], "Use mainnet")
      Option<bool>([|"--testnet"|], "Use testnet")
      Option<bool>([|"--regtest"|], "Use testnet")
    ]

  module private LoopOut =
    let handle (sp: IServiceProvider) (req: LoopOutRequest) (console: IConsole) =
        (async {
            return failwith ""
        }) |> Async.StartAsTask :> Task
    let command(sp): Command =
      let command = Command("out", "Perform Reverse submarine swap and get inbound liquidity")
      let _ =
        let o = Option<uint64>("--channel, -c", "The channel to loop out, you can specify more than once")
        let a = Argument<uint64>()
        a.Arity <- ArgumentArity.ZeroOrMore
        o.Argument <- a
        command.AddOption(o)
      command.Handler <-
        CommandHandler.Create(Func<_,_>(handle sp))
      command

  module private LoopIn =
    let handle (sp: IServiceProvider) (req: LoopOutRequest)  =
        (async {
            return failwith ""
        }) |> Async.StartAsTask :> Task
    let handler(sp) = CommandHandler.Create(Func<_,_>(handle sp))
    let command(sp: IServiceProvider): Command =
      let command = Command("in", "Perform submarine swap And get outbound liquidity")
      command.AddOption(new Option("", ""))
      command.Handler <- handler sp
      command

  module private CommandBase =
    let handle (req: unit) (console: IConsole) =
        (async {
            return failwith ""
        }) |> Async.StartAsTask :> Task
    let command: Command =
      let command = Command("", "")
      let handler = CommandHandler.Create(Func<_,_,_>(handle))
      command

  let getRootCommand() =
    let rc = RootCommand()
    rc.Name <- "nloop"
    rc.Description <- "cli command to interact with nloopd"
    for o in getOptions do
      rc.AddOption(o)
    rc.AddValidator(CommandLineValidators.networkValidator)

    let serviceProvider =
      let s = ServiceCollection()
      s.AddSingleton<BoltzClient>()
        .BuildServiceProvider()

    rc.AddCommand(LoopOut.command serviceProvider)
    rc.AddCommand(LoopIn.command serviceProvider)
    rc
