namespace NLoop.Server.Services

open System.Runtime.CompilerServices
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection

open NLoop.Infrastructure
open NLoop.Server

[<AbstractClass;Sealed;Extension>]
type NLoopExtensions() =
  [<Extension>]
  static member AddNLoopServices(this: IServiceCollection, conf: IConfiguration) =
      let n = conf.GetChainName()
      let addr = conf.GetOrDefault("boltz-url", Constants.DefaultBoltzServer)
      let port = conf.GetOrDefault("boltz-port", Constants.DefaultBoltzPort)
      let boltzClient = BoltzClient(addr, port, n)
      this.AddSingleton(boltzClient)
