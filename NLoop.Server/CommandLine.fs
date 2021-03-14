namespace NLoop.Server

open System
open System.Collections.Generic
open System.IO
open System.Runtime.CompilerServices
open System.CommandLine
open System.CommandLine.Parsing
open Microsoft.Extensions.Configuration
open NBitcoin
open NLoop.Infrastructure

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

module NLoopServerCommandLine =
  let getOptions(): Option seq =
    seq [
      let networkNames = Network.GetNetworks() |> Seq.map(fun n -> n.Name) |> Array.ofSeq
      let o = System.CommandLine.Option<string>([| "-n"; "--network" |], $"Set the network from ({String.Join(',', networkNames)}) (default: mainnet)")
      o.Argument <-
        let a = Argument<string>()
        a.Arity <- ArgumentArity.ExactlyOne
        a.FromAmong(networkNames)
      o

      Option<bool>([|"--mainnet"|], "Use mainnet")
      Option<bool>([|"--testnet"|], "Use testnet")
      Option<bool>([|"--regtest"|], "Use testnet")

      let o = Option<DirectoryInfo>([|"--datadir"; "-d"|], "Directory to store data")
      o.Argument <-
        let a = Argument<DirectoryInfo>()
        a.Arity <- ArgumentArity.ExactlyOne
        a.SetDefaultValue(Constants.DefaultDataDirectoryPath)
        a
      o
      Option<bool>("--nohttps", "Do not use https")
      let o = Option<int>("--https.port", "Port for listening HTTPs request")
      o.Argument <-
        let a = Argument<int>()
        a.Arity <- ArgumentArity.OneOrMore
        a.SetDefaultValue(Constants.DefaultHttpsPort)
        a
      o
      let o = Option<FileInfo>("--https.cert", "Path to the https certification file")
      o.Argument <-
        let a = Argument<FileInfo>()
        a.Arity <- ArgumentArity.ExactlyOne
        a.SetDefaultValue(Constants.DefaultHttpsCertFile)
        a
      o

      let o = Option<FileInfo>("--rpc.cookiefile", "RPC authentication method 1: The RPC Cookiefile (default: using cookie auth from default network folder)")
      o.Argument <-
        let a = Argument<FileInfo>()
        a.Arity <- ArgumentArity.ExactlyOne
        a.SetDefaultValue(Constants.DefaultCookieFile)
        a
      o

      Option<bool>("--noauth", "Disable cookie authentication")

    ]


  let getRootCommand() =
    let rc = RootCommand()
    rc.Name <- "nloopd"
    rc.Description <- "Daemon to manage your LN node with submarine swaps"
    for o in getOptions() do
      rc.AddOption(o)
    rc.AddValidator(CommandLineValidators.networkValidator)
    rc

[<AbstractClass;Sealed;Extension>]
type CommandLineExtensions() =
  [<Extension>]
  static member AddCommandLineOptions(conf: IConfigurationBuilder, commandLine: ParseResult) =
    if commandLine |> isNull then raise <| ArgumentNullException(nameof(commandLine)) else

    let d = Dictionary<string, string>()
    for op in NLoopServerCommandLine.getOptions() do
      if (op.Name = "nloop" || op.Name = "help") then () else
      let s = op.Name.Replace(".", ":").Replace("_", "")
      let v = commandLine.CommandResult.GetArgumentValueOrDefault(op.Name)
      if (v |> isNull |> not) then
        d.Add(s, v.ToString())
    conf.AddInMemoryCollection(d)
