namespace NLoop.CLI

open System
open System.CommandLine.Parsing
open System.Runtime.CompilerServices
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open NLoop.Server
open NLoopClient


[<AbstractClass;Sealed;Extension>]
type Extensions() =

  [<Extension>]
  static member Configure(this: NLoopClient, opts: NLoopOptions) =
    let protocol = if opts.NoHttps then "http" else "http"
    this.BaseUrl <- $"{protocol}://{opts.RPCHost}:{opts.RPCPort}"

  [<Extension>]
  static member GetNLoopClient(this: IServiceProvider) =
    let cli = this.GetRequiredService<NLoopClient>()
    let opts = this.GetRequiredService<IOptions<NLoopOptions>>()
    cli.Configure(opts.Value)
    cli
