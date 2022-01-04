namespace NLoop.Server.Services

open System
open System.CommandLine
open System.CommandLine.Binding
open System.CommandLine.Hosting
open System.Threading.Channels
open System.Threading.Tasks
open BoltzClient
open DotNetLightning.Utils.Primitives
open EventStore.ClientAPI
open EventStore.ClientAPI
open LndClient
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Internal
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.RPC
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server
open System.Runtime.CompilerServices
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection

open NLoop.Server.Options
open NLoop.Server.SwapServerClient
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
          )
        .BindCommandLine()
        .Configure<IServiceProvider>(fun opts serviceProvider ->
          let config = serviceProvider.GetService<IConfiguration>()
          let bindingContext = serviceProvider.GetService<BindingContext>()
          for c in Enum.GetValues<SupportedCryptoCode>() do
            let cOpts =
              let network = c.ToNetworkSet().GetNetwork(opts.ChainName)
              c.GetDefaultOptions(network)
            cOpts.CryptoCode <- c
            for p in typeof<IChainOptions>.GetProperties() do
              let op =
                let optsString = getChainOptionString(c) (p.Name.ToLowerInvariant())
                bindingContext.ParseResult.ValueForOption(optsString)
              let tyDefault = if p.PropertyType = typeof<String> then String.Empty |> box else Activator.CreateInstance(p.PropertyType)
              if op <> null && op <> tyDefault && op.GetType() = p.PropertyType then
                p.SetValue(cOpts, op)
            config.GetSection(c.ToString()).Bind(cOpts)
            opts.ChainOptions.Add(c, cOpts)
          )
        |> ignore

      if (not <| test) then
        this
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<ILightningClientProvider>() :?> LightningClientProvider :> IHostedService
          )
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<IOnGoingSwapStateProjection>() :?> OnGoingSwapStateProjection :> IHostedService
          )
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<IRecentSwapFailureProjection>() :?> RecentSwapFailureProjection :> IHostedService
          )
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<AutoLoopManager>() :> IHostedService
          )
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<IBlockChainListener>() :?> BlockchainListeners :> IHostedService
          )
          .AddSingleton<IHostedService>(fun p -> p.GetRequiredService<BoltzListener>() :> IHostedService)
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<ExchangeRateProvider>() :> IHostedService
          )
          |> ignore

      this
        .AddSingleton<IEventStoreConnection>(fun sp ->
          let opts = sp.GetRequiredService<IOptions<NLoopOptions>>()
          let connSettings =
            ConnectionSettings.Create().DisableTls().Build()
          let conn = EventStoreConnection.Create(connSettings, opts.Value.EventStoreUrl |> Uri)
          do conn.ConnectAsync().GetAwaiter().GetResult()
          conn
        )
        |> ignore

      this
        .AddSingleton<AutoLoopManager>()
        |> ignore

      this
        .AddSingleton<ISystemClock, SystemClock>()
        .AddSingleton<IRecentSwapFailureProjection, RecentSwapFailureProjection>()
        .AddSingleton<IOnGoingSwapStateProjection, OnGoingSwapStateProjection>()
        .AddSingleton<ILightningClientProvider, LightningClientProvider>()
        .AddSingleton<BoltzListener>()
        .AddSingleton<ISwapEventListener, BoltzListener>(fun sp -> sp.GetRequiredService<BoltzListener>())
        .AddSingleton<GetSwapKey>(Func<IServiceProvider, GetSwapKey>(fun _ () -> new Key() |> Task.FromResult))
        .AddSingleton<GetSwapPreimage>(Func<IServiceProvider, GetSwapPreimage>(fun _ () ->
            RandomUtils.GetBytes 32 |> PaymentPreimage.Create |> Task.FromResult
          )

        )
        .AddSingleton<GetAllEvents<Swap.Event>>(Func<IServiceProvider, GetAllEvents<Swap.Event>>(fun sp ct ->
            let conn = sp.GetRequiredService<IEventStoreConnection>()
            conn.ReadAllEventsAsync(Swap.entityType, Swap.serializer, ct)
          )
        )
        |> ignore

      this
        .AddSingleton<ExchangeRateProvider>()
        .AddSingleton<TryGetExchangeRate>(Func<IServiceProvider,_> (fun sp ->
          sp.GetService<ExchangeRateProvider>().TryGetExchangeRate >> Task.FromResult
        ))
        |> ignore
      // Workaround to register one instance as a multiple interfaces.
      // see: https://github.com/aspnet/DependencyInjection/issues/360
      this.AddSingleton<BlockchainListeners>()
        .AddSingleton<ISwapEventListener>(fun sp -> sp.GetRequiredService<BlockchainListeners>() :> ISwapEventListener)
        .AddSingleton<IBlockChainListener>(fun sp -> sp.GetRequiredService<BlockchainListeners>() :> IBlockChainListener)
        |> ignore
      this
        .AddSingleton<GetBlockchainClient>(Func<IServiceProvider,_> (fun sp -> sp.GetService<IOptions<NLoopOptions>>().Value.GetBlockChainClient))
        .AddSingleton<GetWalletClient>(Func<IServiceProvider, _> (fun sp -> sp.GetService<IOptions<NLoopOptions>>().Value.GetWalletClient))
        |> ignore
      this
        .AddSingleton<ISwapServerClient, BoltzSwapServerClient>()
        .AddHttpClient<BoltzClient>()
        .ConfigureHttpClient(fun sp client ->
          client.BaseAddress <- sp.GetRequiredService<IOptions<NLoopOptions>>().Value.BoltzUrl
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
        .AddSingleton<ILightningInvoiceProvider, LightningInvoiceProvider>()
        .AddSingleton<IFeeEstimator, RPCFeeEstimator>()
        .AddSingleton<IUTXOProvider, BitcoinUTXOProvider>()
        .AddSingleton<GetAddress>(fun sp -> sp.GetRequiredService<ILightningClientProvider>().AsChangeAddressGetter())
        .AddSingleton<IEventAggregator, ReactiveEventAggregator>()
        .AddSingleton<ISwapActor, SwapActor>()
        .AddSingleton<ISwapExecutor, SwapExecutor>()

