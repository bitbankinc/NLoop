namespace NLoop.CLI

open System.CommandLine.Parsing
open System.Runtime.CompilerServices
open Microsoft.Extensions.Configuration
open NLoop.Server
open NLoopClient


[<AbstractClass;Sealed;Extension>]
type Extensions() =

  [<Extension>]
  static member Configure(this: NLoopClient, opts: NLoopOptions) =
    let protocol = if opts.NoHttps then "http" else "http"
    this.BaseUrl <- $"{protocol}://{opts.RPCHost}:{opts.RPCPort}"
