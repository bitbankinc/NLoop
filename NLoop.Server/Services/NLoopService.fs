namespace NLoop.Server.Services

open System
open System.CommandLine
open System.CommandLine.Binding
open System.CommandLine.Hosting
open System.Threading.Channels
open LndClient
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open System.Runtime.CompilerServices
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open NLoop.Server.Actors
open NLoop.Server.ProcessManagers
open NLoop.Server.Projections

[<AbstractClass;Sealed;Extension>]
type NLoopExtensions() =
  [<Extension>]
  static member AddNLoopServices(this: IServiceCollection, ?test: bool) =
      let test = defaultArg test false
      this
        .AddOptions<NLoopOptions>()
        .Configure<IServiceProvider>(fun opts serviceProvider ->
          let config = serviceProvider.GetService<IConfiguration>()
          config.Bind(opts)
          let bindingContext = serviceProvider.GetService<BindingContext>()
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

          .AddSingleton<ISwapEventListener, BoltzListener>()
          .AddSingleton<ISwapEventListener, BlockchainListener>()
          .AddHostedService<SwapEventListeners>()

          .AddSingleton<SwapStateProjection>()
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<SwapStateProjection>() :> IHostedService
          )
          |> ignore
      this
        .AddHttpClient<BoltzClient>()
        .ConfigureHttpClient(fun sp client ->
          let opts = sp.GetRequiredService<IOptions<NLoopOptions>>().Value
          client.BaseAddress <-
            let u = UriBuilder($"{opts.BoltzHost}:{opts.BoltzPort}")
            u.Scheme <- if opts.BoltzHttps then "https" else "http"
            u.Uri
        )
        |> ignore
      this
        .AddSignalR()
        .AddJsonProtocol(fun opts ->
          opts.PayloadSerializerOptions.AddNLoopJsonConverters()
        )
        |> ignore

      this
        .AddHostedService<SwapProcessManager>()
        |> ignore

      this
        .AddSingleton<ICheckpointDB, FlatFileCheckpointDB>()
        .AddSingleton<IBroadcaster, BitcoinRPCBroadcaster>()
        .AddSingleton<IFeeEstimator, BoltzFeeEstimator>()
        .AddSingleton<IUTXOProvider, BitcoinUTXOProvider>()
        .AddSingleton<GetAddress>(fun sp -> sp.GetRequiredService<ILightningClientProvider>().AsChangeAddressGetter())
        .AddSingleton<IEventAggregator, ReactiveEventAggregator>()
        .AddSingleton<SwapActor>()

