namespace NLoop.Server.Services

open System
open System.CommandLine.Binding
open System.CommandLine.Hosting
open System.Threading.Channels
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open System.Runtime.CompilerServices
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open NLoop.Server.Actors

[<AbstractClass;Sealed;Extension>]
type NLoopExtensions() =
  [<Extension>]
  static member AddNLoopServices(this: IServiceCollection, conf: IConfiguration, ?test: bool) =
      let test = defaultArg test false
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
              let op =
                let optsString = getChainOptionString(c) (p.Name.ToLowerInvariant())
                bindingContext.ParseResult.ValueForOption(optsString)
              let tyDefault = if p.PropertyType = typeof<String> then String.Empty |> box else Activator.CreateInstance(p.PropertyType)
              if op <> null && op <> tyDefault && op.GetType() = p.PropertyType then
                p.SetValue(cOpts, op)
            config.GetSection(c.ToString()).Bind(cOpts)
            opts.ChainOptions.Add(c, cOpts)
          )
        .BindCommandLine()
        |> ignore

      if (not <| test) then
        this
          .AddSingleton<ILightningClientProvider, LightningClientProvider>()
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<ILightningClientProvider>() :?> LightningClientProvider :> IHostedService
          )
          .AddSingleton<IRepositoryProvider, RepositoryProvider>()
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<IRepositoryProvider>() :?> RepositoryProvider :> IHostedService
          )
        |> ignore

      this
        .AddSingleton<BoltzClientProvider>(BoltzClientProvider(fun n -> BoltzClient(addr, port, n)))
        .AddSingleton<IBroadcaster, BitcoinRPCBroadcaster>()
        .AddSingleton<IFeeEstimator, BoltzFeeEstimator>()
        .AddSingleton<EventAggregator>()
        .AddHostedService<SwapEventListeners>()
        .AddSingleton<SwapActor>()

