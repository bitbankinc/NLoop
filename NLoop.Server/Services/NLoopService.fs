namespace NLoop.Server.Services

open System
open System.CommandLine.Binding
open System.CommandLine.Hosting
open System.Threading.Channels
open Microsoft.Extensions.Logging
open NLoop.Domain
open NLoop.Server
open System.Runtime.CompilerServices
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open NLoop.Server.Actors

[<AbstractClass;Sealed;Extension>]
type NLoopExtensions() =
  [<Extension>]
  static member AddNLoopServices(this: IServiceCollection, conf: IConfiguration) =
      let n = conf.GetChainName()
      let addr = conf.GetOrDefault("boltz-url", Constants.DefaultBoltzServer)
      let port = conf.GetOrDefault("boltz-port", Constants.DefaultBoltzPort)
      this
        .AddOptions<NLoopOptions>()
        .Configure<IServiceProvider>(fun opts serviceProvider ->
          let config = serviceProvider.GetService<IConfiguration>()
          let bindingContext = serviceProvider.GetService<BindingContext>()
          config.Bind(opts)
          for c in Enum.GetValues<SupportedCryptoCode>() do
            let cOpts = ChainOptions()
            cOpts.CryptoCode <- c
            for p in typeof<ChainOptions>.GetProperties() do
              let op = bindingContext.ParseResult.ValueForOption($"--{c.ToString().ToLowerInvariant()}.{p.Name.ToLowerInvariant()}")
              let tyDefault = if p.PropertyType = typeof<String> then String.Empty |> box else Activator.CreateInstance(p.PropertyType)
              if op <> null && op <> tyDefault && op.GetType() = p.PropertyType then
                p.SetValue(cOpts, op)
            config.GetSection(c.ToString()).Bind(cOpts)
            opts.ChainOptions.Add(c, cOpts)
          )
        .BindCommandLine()
        |> ignore

      this
        .AddSingleton<BoltzClientProvider>(BoltzClientProvider(fun n -> BoltzClient(addr, port, n)))
        .AddSingleton<IRepositoryProvider, RepositoryProvider>()
        .AddHostedService<RepositoryProvider>()
        .AddSingleton<IBroadcaster, BitcoinRPCBroadcaster>()
        .AddSingleton<IFeeEstimator, BoltzFeeEstimator>()
        .AddSingleton<EventAggregator>()
        .AddHostedService<LightningClientProvider>()
        |> ignore

      this.AddSingleton<SwapActor>()
