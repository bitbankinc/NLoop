namespace NLoop.Server.Services

open System.Threading.Channels
open NLoop.Server
open System.Runtime.CompilerServices
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection

[<AbstractClass;Sealed;Extension>]
type NLoopExtensions() =
  [<Extension>]
  static member AddNLoopServices(this: IServiceCollection, conf: IConfiguration) =
      let n = conf.GetChainName()
      let addr = conf.GetOrDefault("boltz-url", Constants.DefaultBoltzServer)
      let port = conf.GetOrDefault("boltz-port", Constants.DefaultBoltzPort)
      this
        .AddSingleton<BoltzClientProvider>(BoltzClientProvider(fun n -> BoltzClient(addr, port, n)))
        .AddSingleton<RepositoryProvider>()
        .AddSingleton(Channel.CreateBounded<SwapEvent>(500))
