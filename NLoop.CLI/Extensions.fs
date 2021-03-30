namespace NLoop.CLI

open System.CommandLine.Parsing
open System.Runtime.CompilerServices
open Microsoft.Extensions.Configuration
open NLoopClient


[<AbstractClass;Sealed;Extension>]
type Extensions() =

  [<Extension>]
  static member Configure(this: NLoopClient, conf: IConfiguration, pr: ParseResult) =
    let rpcHost = conf.GetValue("rpchost", pr.ValueForOption<string>("--rpchost"))
    let rpcPort = conf.GetValue("rpcpost", pr.ValueForOption<int>("--rpcport"))
    let protocol = if pr.ValueForOption<bool>("--nohttps") then "http" else "https"
    this.BaseUrl <- $"{protocol}://{rpcHost}:{rpcPort}"
