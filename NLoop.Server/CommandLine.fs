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

module NLoopServerCommandLine =
  module Validators =
    let private networkValidator = ValidateSymbol<_>(fun (r: CommandResult) ->
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

    let getValidators =
      seq [networkValidator]

  let rpcOptions =
    seq [
      let httpsOptions = seq [
        let o = Option<bool>("--nohttps", "Do not use https")
        o.Argument <-
          let a = Argument<bool>()
          a.Arity <- ArgumentArity.ExactlyOne
          a.SetDefaultValue false
          a
        o :> Option
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
      ]
      yield! httpsOptions

      let httpRpcOptions = seq [
        let o = Option<FileInfo>("--rpccookiefile", (fun () -> FileInfo(Constants.DefaultCookieFile)), "RPC authentication method 1: The RPC Cookiefile")
        o.Argument <-
          let a = Argument<FileInfo>()
          a.Arity <- ArgumentArity.ExactlyOne
          a.SetDefaultValue Constants.DefaultCookieFile
          a
        o :> Option
        let o = Option<string>("--rpchost", (fun () -> "localhost"), "host which server listens for rpc call")
        o.Argument <-
          let a = Argument<string>()
          a.Name <- "rpchost"
          a.SetDefaultValue "localhost"
          a
        o
        let o = Option<int>("--rpcport", (fun () -> 5000), "port which server listens for rpc call")
        o.Argument <-
          let a = Argument<int>()
          a.Name <- "rpcport"
          a.SetDefaultValue(5000)
          a
        o
      ]
      yield! httpRpcOptions

      Option<bool>("--noauth", "Disable cookie authentication")
    ]
  let optionsForBothCliAndServer =
    seq [
      let networkNames = Network.GetNetworks() |> Seq.map(fun n -> n.Name) |> Array.ofSeq
      let o = System.CommandLine.Option<string>([| "-n"; "--network" |], $"Set the network from ({String.Join(',', networkNames)}) (default: mainnet)")
      o.Argument <-
        let a = Argument<string>()
        a.Arity <- ArgumentArity.ExactlyOne
        a.FromAmong(networkNames)
      o :> Option
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

      yield! rpcOptions
    ]
  let getOptions(): Option seq =
    optionsForBothCliAndServer

  let getRootCommand() =
    let rc = RootCommand()
    rc.Name <- "nloopd"
    rc.Description <- "Daemon to manage your LN node with submarine swaps"
    for o in getOptions() do
      rc.AddOption(o)
    for v in Validators.getValidators do
      rc.AddValidator(v)
    rc

