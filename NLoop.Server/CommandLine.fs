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
        let o = Option<string>($"--{nameof(NLoopOptions.Instance.HttpsCertPass).ToLowerInvariant()}", "Password to encrypt https cert file.")
        o.Argument <-
          let a = Argument<string>()
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

  let getChainOptions(c) =
     let b = getChainOptionString (c)
     seq [
       let o = Option<string>(b (nameof(ChainOptions.Instance.RPCHost)),
                              "RPC host name of the blockchain client")
       o.Argument <-
         let a = Argument<string>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o :> Option
       let o = Option<int>(b (nameof(ChainOptions.Instance.RPCPort)),
                              "RPC port number of the blockchain client")
       o.Argument <-
         let a = Argument<int>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o
       let o = Option<string>(b (nameof(ChainOptions.Instance.RPCUser)),
                              "RPC username of the blockchain client")
       o.Argument <-
         let a = Argument<string>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o

       let o = Option<string>(b (nameof(ChainOptions.Instance.RPCPassword)),
                              "RPC password of the blockchain client")
       o.Argument <-
         let a = Argument<string>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o

       let o = Option<string>(b (nameof(ChainOptions.Instance.RPCCookieFile)),
                              "RPC cookie file path of the blockchain client")
       o.Argument <-
         let a = Argument<string>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o

       // -- swap fee limitations --
       let o = Option<int64>($"--{nameof(ChainOptions.Instance.MaxSwapFee).ToLowerInvariant()}",
                             "Maximum we are willing to pay the server for the swap. This value is not disclosed in the\n
                             swap initiation call, but if the server asks for a higher fee, we abort the swap.\n
                             You can also specify as a json body when sending a request.")
       o.Argument <-
         let a = Argument<int64>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o
       let o = Option<int64>($"--{nameof(ChainOptions.Instance.MinimumSwapAmountSats).ToLowerInvariant()}",
                             $"Minimum Swap amount we can perform. (default: {ChainOptions.Instance.MinimumSwapAmountSats})\n
                               This is a hard limit. You can also specify as a json body when sending a request.")
       o.Argument <-
         let a = Argument<int64>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o

       let o = Option<int64>($"--{nameof(ChainOptions.Instance.MaxPrepayAmountSats).ToLowerInvariant()}",
                             $"Maximum amount you accept as an prepay fee for a loop out.\n
                             This is a hard limit. you can also specify as a json body ")
       o.Argument <-
         let a = Argument<int64>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o
       let o = Option<int64>($"--{nameof(ChainOptions.Instance.MaxPrepayAmountSats).ToLowerInvariant()}", $"Maximum amount you accept as an prepay fee for loop out.")
       o.Argument <-
         let a = Argument<int64>()
         a.Arity <- ArgumentArity.ZeroOrOne
         a
       o
       // -- --

     ]

  let getOptions(): Option seq =
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


      let o = Option<bool>($"--{nameof(NLoopOptions.Instance.AcceptZeroConf).ToLowerInvariant()}", "Whether we want to accept zero conf")
      o.Argument <-
        let a = Argument<bool>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a
      o

      let o = Option<SupportedCryptoCode[]>($"--{nameof(NLoopOptions.Instance.OnChainCrypto).ToLowerInvariant()}",
                                            $"the cryptocode we want to support for on-chain swap (default: %A{NLoopOptions.Instance.OnChainCrypto})")
      o.Argument <-
        let a = Argument<SupportedCryptoCode[]>()
        a.Arity <- ArgumentArity.ZeroOrMore
        a
      o
      let o = Option<SupportedCryptoCode[]>($"--{nameof(NLoopOptions.Instance.OffChainCrypto).ToLowerInvariant()}",
                                            $"the cryptocode we want to support for off-chain swap (default: %A{NLoopOptions.Instance.OffChainCrypto})")
      o.Argument <-
        let a = Argument<SupportedCryptoCode[]>()
        a.Arity <- ArgumentArity.ZeroOrMore
        a
      o
      for c in Enum.GetValues<SupportedCryptoCode>() do
        yield! getChainOptions c

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
      let o = Option<bool>($"--boltzhttps", $"Whether to use https for the boltz server or not (default: {Constants.DefaultBoltzHttps})")
      o.Argument <-
        let a = Argument<bool>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a
      o

      let o = Option<string>([|$"--{nameof(NLoopOptions.Instance.EventStoreUrl).ToLowerInvariant()}";|],
                             $"Url for your eventstore db. (default: {NLoopOptions.Instance.EventStoreUrl})")
      o.Argument <-
        let a = Argument<string>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a
      o
      // --- lnd ---

      let o = Option<string>($"--{nameof(NLoopOptions.Instance.LndCert).ToLowerInvariant()}",
                             "Path to the TLS certification of the LND")
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

      let o = Option<bool>($"--{nameof(NLoopOptions.Instance.LndAllowUnsafe).ToLowerInvariant()}",
                             "Allow connection to the LND without ssl/tls (intended for test use)")
      o.Argument <-
        let a = Argument<bool>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a
      o
       // --- ---
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

