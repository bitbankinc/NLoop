namespace NLoop.Server

open System
open System.Collections.Generic
open System.CommandLine
open System.IO
open System.Runtime.CompilerServices
open System.CommandLine
open System.CommandLine.Parsing
open Microsoft.Extensions.Configuration
open NBitcoin
open NLoop.Server

module NLoopServerCommandLine =
  module Validators =
    let getValidators =
      seq []

  let rpcOptions =
    seq [
      let httpsOptions = seq [
        let o = Option<bool>($"--{nameof(NLoopOptions.Instance.NoHttps).ToLowerInvariant()}", "Do not use https")
        o.Argument <-
          let a = Argument<bool>()
          a.Arity <- ArgumentArity.ExactlyOne
          a
        o :> Option
        let o = Option<int>($"--{nameof(NLoopOptions.Instance.HttpsPort).ToLowerInvariant()}", "Port for listening HTTPs request")
        o.Argument <-
          let a = Argument<int>()
          a.Arity <- ArgumentArity.ExactlyOne
          a
        o
        let o = Option<FileInfo>($"--{nameof(NLoopOptions.Instance.HttpsCert).ToLowerInvariant()}", "Path to the https certification file")
        o.Argument <-
          let a = Argument<FileInfo>()
          a.Arity <- ArgumentArity.ExactlyOne
          a
        o
      ]
      yield! httpsOptions

      let httpRpcOptions = seq [
        let o = Option<FileInfo>($"--{nameof(NLoopOptions.Instance.RPCCookieFile).ToLowerInvariant()}", "RPC authentication method 1: The RPC Cookiefile")
        o.Argument <-
          let a = Argument<FileInfo>()
          a.Arity <- ArgumentArity.ExactlyOne
          a
        o :> Option
        let o = Option<string>($"--{nameof(NLoopOptions.Instance.RPCHost).ToLowerInvariant()}", "host which server listens for rpc call")
        o.Argument <-
          let a = Argument<string>()
          a.Arity <- ArgumentArity.ExactlyOne
          a
        o
        let o = Option<int>($"--{nameof(NLoopOptions.Instance.RPCPort).ToLowerInvariant()}", "port which server listens for rpc call")
        o.Argument <-
          let a = Argument<int>()
          a.Arity <- ArgumentArity.ExactlyOne
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
      let o = Option<DirectoryInfo>([|$"--{nameof(NLoopOptions.Instance.DataDir).ToLowerInvariant()}"; "-d"|], "Directory to store data")
      o.Argument <-
        let a = Argument<DirectoryInfo>()
        a.Arity <- ArgumentArity.ExactlyOne
        a.SetDefaultValue(Constants.DefaultDataDirectoryPath)
        a
      o

      yield! rpcOptions
    ]
  let getOptions(): Option seq =
    seq [
      yield! optionsForBothCliAndServer
      let o = Option<string[]>($"--{nameof(NLoopOptions.Instance.RPCAllowIP).ToLowerInvariant()}", "rpc allow ip")
      o.Argument <-
        let a = Argument<string[]>()
        a.Arity <- ArgumentArity.ZeroOrMore
        a
      o

      let o = Option<int64>($"--{nameof(NLoopOptions.Instance.MaxAcceptableSwapFee).ToLowerInvariant()}")
      o.Argument <-
        let a = Argument<int64>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a
      o
    ]

  let getRootCommand() =
    let rc = RootCommand()
    rc.Name <- "nloopd"
    rc.Description <- "Daemon to manage your LN node with submarine swaps"
    for o in getOptions() do
      rc.AddOption(o)
    for v in Validators.getValidators do
      rc.AddValidator(v)
    rc

