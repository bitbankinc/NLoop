namespace NLoop.Server.Services

open System
open System.CommandLine
open System.CommandLine.Binding
open System.CommandLine.Hosting
open System.Threading
open System.Threading.Tasks
open DotNetLightning.ClnRpc.Plugin
open ExchangeSharp
open FSharp.Control.Tasks
open BoltzClient
open DotNetLightning.Utils.Primitives
open EventStore.ClientAPI
open LndClient
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Internal
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
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

type GetEventStoreConnection = unit -> Task<IEventStoreConnection>

[<AbstractClass;Sealed;Extension>]
type NLoopExtensions() =
  
  [<Extension>]
  static member BindChainOptions(opts: NLoopOptions,
                                 getOptionFromKeyName: SupportedCryptoCode -> string -> obj,
                                 additionalBinding: SupportedCryptoCode -> IChainOptions -> unit) =
    for c in Enum.GetValues<SupportedCryptoCode>() do
      let cOpts =
        let network = c.ToNetworkSet().GetNetwork(opts.ChainName)
        c.GetDefaultOptions(network)
      cOpts.CryptoCode <- c
      for p in typeof<IChainOptions>.GetProperties() do
        let op =
          getOptionFromKeyName c (p.Name.ToLowerInvariant())
        let tyDefault =
          if p.PropertyType = typeof<String> then
            String.Empty |> box
          else
            Activator.CreateInstance(p.PropertyType)
        if op <> null && op <> tyDefault then
          try
            let t = Convert.ChangeType(op, p.PropertyType)
            if t <> tyDefault then
              p.SetValue(cOpts, t)
          with
          | :? InvalidCastException ->
            ()
        additionalBinding c cOpts
        opts.ChainOptions.AddOrReplace(c, cOpts)

  [<Extension>]
  static member AddNLoopServices(this: IServiceCollection, ?coldStart: bool) =
      let coldStart = defaultArg coldStart false
      this
        .AddOptions<NLoopOptions>()
        .Configure<IServiceProvider>(fun opts serviceProvider ->
          let config = serviceProvider.GetService<IConfiguration>()
          if config |> isNull |> not then
            config.Bind(opts)
          )
        .BindCommandLine()
        .Configure<IServiceProvider>(fun opts serviceProvider ->
          let config = serviceProvider.GetService<IConfiguration>()
          if config |> isNull then () else
          let bindingContext = serviceProvider.GetService<BindingContext>()
          
          opts.BindChainOptions(
            (fun c p -> getChainOptionString c (p.ToLowerInvariant()) |> bindingContext.ParseResult.ValueForOption),
            fun c -> config.GetSection(c.ToString()).Bind
            )
          )
        |> ignore

      this
        .AddSingleton<GetEventStoreConnection>(Func<IServiceProvider, _> (fun sp () ->
          task {
            let opts = sp.GetRequiredService<GetOptions>()()
            let logger = sp.GetRequiredService<ILogger<IEventStoreConnection>>()
            let connSettings =
              ConnectionSettings
                .Create()
                .DisableTls()
                .LimitReconnectionsTo(10)
                .LimitAttemptsForOperationTo(10)
                .LimitRetriesForOperationTo(10)
                .Build()
            let conn = EventStoreConnection.Create(connSettings, opts.EventStoreUrl |> Uri)
            conn.AuthenticationFailed.Add(fun args ->
              logger.LogError $"connection to eventstore ({opts.EventStoreUrl}) failed: {args.Reason}"
            )
            conn.ErrorOccurred.Add(fun args ->
              logger.LogError $"error in eventstore connection: {args.Exception}"
            )
            conn.Disconnected.Add(fun args ->
              logger.LogWarning $"EventStore ({args.RemoteEndPoint.ToEndpointString()}): disconnected."
            )
            conn.Reconnecting.Add(fun _args ->
              logger.LogInformation $"Reconnecting to event store ({opts.EventStoreUrl})... "
            )
            do! conn.ConnectAsync()
            return conn
          }
        ))
        .AddSingleton<GetOptions>(Func<IServiceProvider, GetOptions>(fun sp () ->
          let holder = sp.GetService<NLoopOptionsHolder>()
          if holder |> box |> isNull |> not && holder.NLoopOptions.IsSome then
            holder.NLoopOptions.Value
          else
            let v = sp.GetRequiredService<IOptions<NLoopOptions>>().Value
            v
        ))
        |> ignore


      this
        .AddSingleton<GetStore>(Func<IServiceProvider, GetStore>(fun sp () ->
          let opts = sp.GetRequiredService<GetOptions>()()
          EventStore.eventStore(opts.EventStoreUrl |> Uri)
          )
        )
        .AddSingleton<GetDBSubscription>(Func<IServiceProvider, _>(fun sp parameters ->
          task {
            let opts = sp.GetRequiredService<GetOptions>()()
            let loggerFactory = sp.GetRequiredService<ILoggerFactory>()
            let! conn = sp.GetRequiredService<GetEventStoreConnection>()()
            return EventStoreDBSubscription(
              { EventStoreConfig.Uri = opts.EventStoreUrl |> Uri },
              parameters.Owner,
              parameters.Target,
              loggerFactory.CreateLogger(),
              parameters.HandleEvent,
              parameters.OnFinishCatchUp
              |> Option.map(fun onFinishCatchup -> (fun re -> onFinishCatchup (re |> box))),
              conn)
            :> IDatabaseSubscription
          }
        ))
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
        .AddSingleton<GetAllEvents<Swap.Event>>(Func<IServiceProvider, GetAllEvents<Swap.Event>>(fun sp since ct ->
            task {
              let! conn = sp.GetRequiredService<GetEventStoreConnection>()()
              return!
                match since with
                | Some date ->
                  conn.ReadAllEventsAsync(Swap.entityType, Swap.serializer, date, ct)
                | None ->
                  conn.ReadAllEventsAsync(Swap.entityType, Swap.serializer, ct)
            }
          )
        )
        |> ignore

      this
        .AddSingleton<AutoLoopManagers>()
        .AddSingleton<TryGetAutoLoopManager>(Func<IServiceProvider, _>(fun sp cc ->
          match sp.GetRequiredService<AutoLoopManagers>().Managers.TryGetValue(cc) with
          | true, v -> Some v
          | false, _ -> None
        ))
        |> ignore
      this
        .AddSingleton<ExchangeRateProvider>()
        .AddSingleton<TryGetExchangeRate>(Func<IServiceProvider,_> (fun sp ->
          sp.GetRequiredService<ExchangeRateProvider>().TryGetExchangeRate >> Task.FromResult
        ))
        |> ignore
      // Workaround to register one instance as a multiple interfaces.
      // see: https://github.com/aspnet/DependencyInjection/issues/360
      this.AddSingleton<BlockchainListeners>()
        .AddSingleton<ISwapEventListener>(fun sp -> sp.GetRequiredService<BlockchainListeners>() :> ISwapEventListener)
        .AddSingleton<IBlockChainListener>(fun sp -> sp.GetRequiredService<BlockchainListeners>() :> IBlockChainListener)
        |> ignore
      this
        .AddSingleton<GetBlockchainClient>(Func<IServiceProvider,_> (fun sp cc ->
          (sp.GetService<GetOptions>()()).GetBlockChainClient cc
        ))
        .AddSingleton<GetWalletClient>(Func<IServiceProvider, _> (fun sp cc ->
          let opts = sp.GetService<GetOptions>()()
          if opts.OffChainCrypto |> Array.contains cc then
            sp.GetRequiredService<ILightningClientProvider>().GetClient(cc)
            :?> NLoopLndGrpcClient :> IWalletClient
          else
            opts.GetWalletClient cc
          )
        )
        |> ignore
      this
        .AddSingleton<ISwapServerClient, BoltzSwapServerClient>()
        .AddHttpClient<BoltzClient>()
        .ConfigureHttpClient(fun sp client ->
          client.BaseAddress <- (sp.GetRequiredService<GetOptions>()()).BoltzUrl
        )
        |> ignore

      this
        .AddSingleton<GetNetwork>(Func<IServiceProvider, _>(fun sp cc ->
          let opts = sp.GetRequiredService<GetOptions>()
          opts().GetNetwork(cc)
          ))
        .AddSingleton<IBroadcaster, BitcoinRPCBroadcaster>()
        .AddSingleton<ILightningInvoiceProvider, LightningInvoiceProvider>()
        .AddSingleton<IFeeEstimator, RPCFeeEstimator>()
        .AddSingleton<GetAddress>(Func<IServiceProvider, GetAddress>(fun sp ->
          GetAddress(fun cc ->
            let opts = sp.GetService<GetOptions>()()
            if opts.OffChainCrypto |> Seq.contains cc then
              let getter = sp.GetRequiredService<ILightningClientProvider>().AsChangeAddressGetter()
              getter.Invoke cc
            else
              let walletClient = sp.GetService<GetWalletClient>()(cc)
              let network = opts.GetNetwork(cc)
              task {
                try
                  let! r = walletClient.GetDepositAddress(network)
                  return Ok r
                with
                | ex ->
                  return Error ex.Message
              }
          )
        ))
        .AddSingleton<IEventAggregator, ReactiveEventAggregator>()
        .AddSingleton<ISwapActor, SwapActor>()
        .AddSingleton<ISwapExecutor, SwapExecutor>()
        |> ignore

      this
        .AddHealthChecks()
        |> ignore

      if (not <| coldStart) then
        // it is important here that Startup order is
        // SwapProcessManager -> OngoingSwapStateProjection -> BlockchainListeners
        // Since otherwise on startup it fails to re-register swaps on blockchain listeners.
        this
          .AddHostedService<SwapProcessManager>()
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<IOnGoingSwapStateProjection>() :?> OnGoingSwapStateProjection :> IHostedService
          )
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<IBlockChainListener>() :?> BlockchainListeners :> IHostedService
          )
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<IRecentSwapFailureProjection>() :?> RecentSwapFailureProjection :> IHostedService
          )
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<ExchangeRateProvider>() :> IHostedService
          )
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<ILightningClientProvider>() :?> LightningClientProvider :> IHostedService
          )
          .AddSingleton<IHostedService>(fun p ->
            p.GetRequiredService<AutoLoopManagers>() :> IHostedService
          )
          .AddSingleton<IHostedService>(fun p -> p.GetRequiredService<BoltzListener>() :> IHostedService)
          |> ignore
