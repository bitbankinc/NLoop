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

    member this.AddPairIdOption() =
      let o = Option<string>([|"--pair"; "--pair-id"; "-p"|], "The cryptocode pair (e.g. \"BTC/BTC\")")
      o.Name <- "pair_id"
      o.Argument <-
        let a = Argument<string>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a.SetDefaultValue("BTC/BTC")
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

    member this.AddConfRequirementOption() =
      let o = Option<int>([|"--swap-tx-conf-requirement"; "--conf-requirement"|], "Confirmation required for swap.")
      o.Name <- "confirmation_requirement"
      o.Argument <-
        let a = Argument<int>()
        a.Arity <- ArgumentArity.ZeroOrOne
        a.SetDefaultValue(1)
        a
      o.IsRequired <- false
      this.AddOption(o)
      this
