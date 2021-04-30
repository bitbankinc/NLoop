namespace NLoop.CLI.SubCommands

open System.CommandLine
open NLoop.Domain
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
      this

    member this.AddChannelOption() =
      let o = Option<uint64>([|"--channel_id"; "--channel"; "-c"|], "The short channel id which you want to swap")
      o.Name <- "channel_id"
      o.Argument <-
        let a = Argument<uint64>()
        a.Arity <- ArgumentArity.ZeroOrMore
        a
      o.IsRequired <- false
      this.AddOption(o)
      this

    member this.AddLabelOption() =
      let o = Option<string>([|"--label"; "-l"|], "The label you want to set to the swap")
      o.Name <- "label"
      o.Argument <-
        let a = Argument<string>()
        a.Arity  <- ArgumentArity.ZeroOrMore
        a
      o.IsRequired <- false
      this.AddOption(o)
      this

    member this.AddCryptoCodeOption() =
      let o = Option<CryptoCode>([|"--crypto"; "--cryptocode"; "-cc"|], "The cryptocode for our currency (default: BTC)")
      o.Name <- "crypto_code"
      o.Argument <-
        let a = Argument<CryptoCode>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a.SetDefaultValue(CryptoCode.BTC)
        a
      o.IsRequired <- false
      this.AddOption(o)
      this

    member this.AddCounterPartyPairOption() =
      let o = Option<CryptoCode>([|"--counterparty-cryptocode"; "-ccc"|], "The cryptocode for counterparty currency (default: BTC)")
      o.Name <- "counter_party_pair"
      o.Argument <-
        let a = Argument<CryptoCode>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a.SetDefaultValue(CryptoCode.BTC)
        a
      o.IsRequired <- false
      this.AddOption(o)
      this

    member this.AddAddressOption() =
      let o = Option<string>([|"--address"; "--addr"|], "on-chain address to sweep out. If not set, it will deposit to your own Lightning node wallet")
      o.Name <- "address"
      o.Argument <-
        let a = Argument<string>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a
      o.IsRequired <- false
      this.AddOption(o)
      this

    member this.AddConfTargetOption() =
      let o = Option<int>([|"--conf-target"; "--confirmation"|], "Confirmation required for swap.")
      o.Name <- "confirmation_target"
      o.Argument <-
        let a = Argument<int>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a.SetDefaultValue(1)
        a
      o.IsRequired <- false
      this.AddOption(o)
      this
