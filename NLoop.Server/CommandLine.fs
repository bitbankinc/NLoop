namespace NLoop.Server

open System
open System.Collections.Generic
open System.CommandLine
open System.IO
open System.Net
open System.Runtime.CompilerServices
open System.CommandLine
open System.CommandLine.Parsing
open Microsoft.Extensions.Configuration
open NBitcoin
open NLoop.Domain
open NLoop.Server
open NLoop.Server.Options

module NLoopServerCommandLine =
  module Validators =
    let getValidators =
      seq [
        let _cryptoCodeValidator = ValidateSymbol(fun (r: CommandResult) ->
          let onChainArgName = nameof(NLoopOptions.Instance.OnChainCrypto)
          let onChain = r.GetArgumentValueOrDefault<SupportedCryptoCode[]>($"--{onChainArgName}")
          let offChainArgName = nameof(NLoopOptions.Instance.OffChainCrypto)
          let offChain = r.GetArgumentValueOrDefault<SupportedCryptoCode[]>($"--{offChainArgName}")
          if (offChain |> Seq.exists(fun off -> onChain |> Seq.contains(off) |> not)) then
            $"{offChainArgName} ({offChain}) should not contains entry which does not exist in {onChainArgName} ({onChain}) "
          else
            null
        )
        // TODO: validate
        //cryptoCodeValidator
        ()
      ]

  let rpcOptions =
    seq [
      let httpsOptions = seq [
        let o = Option<bool>($"--{nameof(NLoopOptions.Instance.NoHttps).ToLowerInvariant()}", "Do not use https")
        o.Argument <-
          let a = Argument<bool>()
          a.Arity <- ArgumentArity.ExactlyOne
          a.SetDefaultValue(NLoopOptions.Instance.NoHttps)
          a
        o :> Option
        let o = Option<int>($"--{nameof(NLoopOptions.Instance.HttpsPort).ToLowerInvariant()}", "Port for listening HTTPs request")
        o.Argument <-
          let a = Argument<int>()
          a.Arity <- ArgumentArity.ExactlyOne
          a.SetDefaultValue(NLoopOptions.Instance.HttpsPort)
          a
        o
        let o = Option<FileInfo>($"--{nameof(NLoopOptions.Instance.HttpsCert).ToLowerInvariant()}", "Path to the https certification file")
        o.Argument <-
          let a = Argument<FileInfo>()
          a.Arity <- ArgumentArity.ExactlyOne
          a.SetDefaultValue(NLoopOptions.Instance.HttpsCert)
          a
        o
        let o = Option<string>($"--{nameof(NLoopOptions.Instance.HttpsCertPass).ToLowerInvariant()}", "Password to encrypt https cert file.")
        o.Argument <-
          let a = Argument<string>()
          a.Arity <- ArgumentArity.ExactlyOne
          a.SetDefaultValue(NLoopOptions.Instance.HttpsCertPass)
          a
        o
      ]
      yield! httpsOptions

      let httpRpcOptions = seq [
        let o = Option<FileInfo>($"--{nameof(NLoopOptions.Instance.RPCCookieFile).ToLowerInvariant()}", "RPC authentication method 1: The RPC Cookiefile")
        o.Argument <-
          let a = Argument<FileInfo>()
          a.Arity <- ArgumentArity.ExactlyOne
          a.SetDefaultValue(NLoopOptions.Instance.RPCCookieFile)
          a
        o :> Option
        let o = Option<string>($"--{nameof(NLoopOptions.Instance.RPCHost).ToLowerInvariant()}", "host which server listens for rpc call")
        o.Argument <-
          let a = Argument<string>()
          a.Arity <- ArgumentArity.ExactlyOne
          a.SetDefaultValue(NLoopOptions.Instance.RPCHost)
          a
        o
        let o = Option<int>($"--{nameof(NLoopOptions.Instance.RPCPort).ToLowerInvariant()}", "port which server listens for rpc call")
        o.Argument <-
          let a = Argument<int>()
          a.Arity <- ArgumentArity.ExactlyOne
          a.SetDefaultValue(NLoopOptions.Instance.RPCPort)
          a
        o
      ]
      yield! httpRpcOptions

      let o = Option<bool>("--noauth", "Disable cookie authentication")
      o
    ]
  let optionsForBothCliAndServer =
    seq [
      let networks = Network.GetNetworks()
      let networkNames = networks |> Seq.map(fun n -> n.Name.ToLowerInvariant()) |> Array.ofSeq
      let o = System.CommandLine.Option<string>([| "-n"; "--network" |], $"Set the network from ({String.Join(',', networkNames)}) (default:{NLoopOptions.Instance.Network.ToLowerInvariant()})")
      o.Argument <-
        let a = Argument<string>()
        a.Arity <- ArgumentArity.ExactlyOne
        a.SetDefaultValue(NLoopOptions.Instance.Network)
        a.FromAmong(networkNames)
      o :> Option
      let o = Option<DirectoryInfo>([|$"--{nameof(NLoopOptions.Instance.DataDir).ToLowerInvariant()}"; "-d"|], "Directory to store data other than those stored in eventstoredb.")
      o.Argument <-
        let a = Argument<DirectoryInfo>()
        a.Arity <- ArgumentArity.ExactlyOne
        a.SetDefaultValue(NLoopOptions.Instance.DataDir)
        a
      o

      yield! rpcOptions
    ]

  let getChainOptions c (networks: Network list) =
     let b = getChainOptionString c
     let opts = c.GetDefaultOptions()
     let nDefaults = networks |> List.map(fun n -> (n, c.GetDefaultOptions(n)))
     seq [
       let getDefaults(chainOptionToField: IChainOptions -> obj) =
         nDefaults
         |> List.fold(fun acc (n, o) -> $"{acc}{n.ToString().ToLowerInvariant()}: {chainOptionToField o}, ") "defaults: ("
         |> (+) <| ")"
       let o =
         let defaults = getDefaults(fun o -> o.RPCHost |> box)
         Option<string>(b (nameof(opts.RPCHost)),
                        $"RPC host name of the blockchain client. {defaults}")
       o.Argument <-
         let a = Argument<string>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o :> Option
       let o =
         let defaults = getDefaults(fun o -> o.RPCPort |> box)
         Option<int>(b (nameof(opts.RPCPort)),
                     $"RPC port number of the blockchain client. {defaults}")
       o.Argument <-
         let a = Argument<int>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o
       let o = Option<string>(b (nameof(opts.RPCUser)),
                              "RPC username of the blockchain client")
       o.Argument <-
         let a = Argument<string>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o

       let o = Option<string>(b (nameof(opts.RPCPassword)),
                              "RPC password of the blockchain client")
       o.Argument <-
         let a = Argument<string>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o

       let o = Option<string>(b (nameof(opts.RPCCookieFile)),
                              "RPC cookie file path of the blockchain client")
       o.Argument <-
         let a = Argument<string>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o

       let o = Option<string>(b (nameof(opts.ZmqHost)),
                              $"optional: zeromq host address. It will fallback to rpc long-polling " +
                              $"if it is unavailable.")
       o.Argument <-
         let a = Argument<string>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o
       let o = Option<int>(b (nameof(opts.ZmqPort)), $"optional: zeromq port")
       o.Argument <-
         let a = Argument<int>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o
       let o = Option<int>(b (nameof(opts.WalletMinConf)), $"We do not use funds from the wallet if it has less confirmation than this value. (default: {opts.WalletMinConf})")
       o.Argument <-
         let a = Argument<int>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o
     ]

  let getOptions(): Option seq =
    let networks = Network.GetNetworks() |> Seq.toList
    seq [
      yield! optionsForBothCliAndServer
      let o = Option<string[]>($"--{nameof(NLoopOptions.Instance.RPCCors).ToLowerInvariant()}",
                               $"Access-Control-Allow-Origin for the rpc (default: %A{NLoopOptions.Instance.RPCCors})")
      o.Argument <-
        let a = Argument<string[]>()
        a.Arity <- ArgumentArity.ZeroOrMore
        a
      o

      let o = Option<string[]>($"--{nameof(NLoopOptions.Instance.RPCAllowIP).ToLowerInvariant()}", "rpc allow ip")
      o.Argument <-
        let a = Argument<string[]>()
        a.Arity <- ArgumentArity.ZeroOrMore
        a
      o

      let o = Option<SupportedCryptoCode[]>($"--{nameof(NLoopOptions.Instance.OnChainCrypto).ToLowerInvariant()}",
                                            $"the cryptocode we want to support for on-chain swap (default: %A{NLoopOptions.Instance.OnChainCrypto}). " +
                                            "You can specify this option more than once")
      o.Argument <-
        let a = Argument<SupportedCryptoCode[]>()
        a.Arity <- ArgumentArity.ZeroOrMore
        a
      o
      let o = Option<SupportedCryptoCode[]>($"--{nameof(NLoopOptions.Instance.OffChainCrypto).ToLowerInvariant()}",
                                            $"the cryptocode we want to support for off-chain swap (default: %A{NLoopOptions.Instance.OffChainCrypto}). " +
                                            "You can specify this option more than once")
      o.Argument <-
        let a = Argument<SupportedCryptoCode[]>()
        a.Arity <- ArgumentArity.ZeroOrMore
        a
      o
      for c in Enum.GetValues<SupportedCryptoCode>() do
        yield! getChainOptions c networks

      let o = Option<string>($"--boltzhost", $"Host name of your boltz server: (default: {Constants.DefaultBoltzServer})")
      o.Argument <-
        let a = Argument<string>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a
      o
      let o = Option<int>($"--boltzport", $"port of your boltz server: (default: {Constants.DefaultBoltzPort})")
      o.Argument <-
        let a = Argument<int>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a
      o

      let o = Option<string>([|$"--{nameof(NLoopOptions.Instance.EventStoreUrl).ToLowerInvariant()}";|],
                             $"Url for your eventstoredb. (default: {NLoopOptions.Instance.EventStoreUrl})")
      o.Argument <-
        let a = Argument<string>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a
      o
      // --- lnd ---

      let o = Option<string>($"--{nameof(NLoopOptions.Instance.LndCertThumbPrint).ToLowerInvariant()}",
                             "Hex encoded sha256 thumbnail of the lnd certificate")
      o.Argument <-
        let a = Argument<string>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a
      o

      let o = Option<string>($"--{nameof(NLoopOptions.Instance.LndMacaroon).ToLowerInvariant()}",
                             "hex-encoded macaroon for the lnd")
      o.Argument <-
        let a = Argument<string>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a
      o

      let o = Option<string>($"--{nameof(NLoopOptions.Instance.LndMacaroonFilePath).ToLowerInvariant()}",
                             "path to the admin macaroon file for the lnd.")
      o.Argument <-
        let a = Argument<string>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a
      o

      let o = Option<string>($"--{nameof(NLoopOptions.Instance.LndGrpcServer).ToLowerInvariant()}",
                             "Host Url for the lnd")
      o.Argument <-
        let a = Argument<string>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a
      o

      let o = Option<bool>($"--{nameof(NLoopOptions.Instance.LndAllowInsecure).ToLowerInvariant()}",
                             "Allow connection to the LND without ssl/tls (intended for test use)")
      o.Argument <-
        let a = Argument<bool>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a
      o
       // --- ---

      let o = Option<string[]>($"--{nameof(NLoopOptions.Instance.Exchanges).ToLowerInvariant()}",
                               "The name of exchanges you want to use, nloop will use its public api to get the current\n" +
                               "exchange rate in case of multi-asset swap. (e.g. for validating fees after adjusting the price)\n" +
                               "The list of possible exchanges are those which supported by NuGetPackage named ExchangeSharp: " +
                               "https://github.com/jjxtra/ExchangeSharp \n" +
                               "you can specify this option multiple times. "+
                               "In that case, the median value of all exchanges will be used.\n" +
                               $"default is {NLoopOptions.Instance.Exchanges |> List.ofSeq}")
      o.Argument <-
        let a = Argument<string[]>()
        a.Arity <- ArgumentArity.ZeroOrMore
        a
      o
    ]


  let getRootCommand() =
    let rc = RootCommand()
    rc.Name <- Constants.AppName
    rc.Description <- "Daemon to manage your LN node with submarine swaps"
    for o in getOptions() do
      rc.AddOption(o)
    for v in Validators.getValidators do
      rc.AddValidator(v)
    rc

