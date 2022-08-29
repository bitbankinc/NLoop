namespace NLoop.Server

open System
open System.Collections.Generic
open System.Threading.Tasks
open LndClient
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open NLoop.Domain
open NLoop.Server

type ClnLightningClientProvider(
    loggerFactory: ILoggerFactory,
    getOptions: GetOptions
    ) =
    do ()
    
    member private this.AllClients =
        lazy(
            let opts = getOptions()
            let clients = Dictionary<SupportedCryptoCode, INLoopLightningClient>()
            for cc in opts.OffChainCrypto do
                let cli =
                    let rpc = opts.ClnRpcFile
                    if rpc |> isNull then failwith "cln rpc file not set." else
                    let uri = Uri($"unix://{rpc}")
                    let chainRpc = opts.GetRPCClient(cc)
                    NLoopCLightningClient(uri, opts.GetNetwork(cc), chainRpc, loggerFactory.CreateLogger<_>())
                    :> INLoopLightningClient
                clients.Add(cc, cli)
            clients
        )
        
    interface ILightningClientProvider with
        member this.Name = "c-lightning"
        member this.GetAllClients() =
            this.AllClients.Value.Values
            
        member this.TryGetClient(crypto) =
          match this.AllClients.Value.TryGetValue(crypto) with
          | true, v -> v |> Some
          | _, _ -> None
          
    // dummy for implementation purpose.
    // so that it can be registered in the same way with LndClientProvider
    interface IHostedService with
        member this.StartAsync(_cancellationToken) = Task.CompletedTask
        member this.StopAsync(_cancellationToken) = Task.CompletedTask
