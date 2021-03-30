namespace NLoop.CLI.SubCommands

open System.CommandLine
open NLoopClient

[<AutoOpen>]
module Helpers =
  type Command with
    member this.AddAmountOption() =
      let o = Option<int64>([|"--amount";"--amt";"-a"|], "An amount you want to swap (in satoshi)")
      o.IsRequired <- true
      o.Name <- "amount"
      o.Argument <-
        let a = Argument<int64>()
        a.Arity <- ArgumentArity.ExactlyOne
        a
      this.AddOption(o)

    member this.AddChannelOption() =
      let o = Option<uint64>([|"--channel_id"; "--channel"; "-c"|], "The short channel id which you want to swap")
      o.Name <- "channel_id"
      o.Argument <-
        let a = Argument<uint64>()
        a.Arity <- ArgumentArity.ZeroOrMore
        a
      o.IsRequired <- false
      this.AddOption(o)

    member this.AddLabelOption() =
      let o = Option<string>([|"--label"; "-l"|], "The label you want to set to the swap")
      o.Name <- "label"
      o.Argument <-
        let a = Argument<string>()
        a.Arity  <- ArgumentArity.ZeroOrMore
        a
      o.IsRequired <- false
      this.AddOption(o)

    member this.AddCryptoCodeOption() =
      let o = Option<CryptoCode>([|"--crypto"; "--cryptocode"|], "The cryptocode for our currency (default: BTC)")
      o.Name <- "crypto_code"
      o.Argument <-
        let a = Argument<CryptoCode>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a.SetDefaultValue(CryptoCode.BTC)
        a
      o
