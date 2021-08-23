namespace NLoop.Server.Services

open System
open System.Threading
open Microsoft.Extensions.Hosting
open FSharp.Control.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Server

type AutoLoopManager(logger: ILogger<AutoLoopManager>,
                     opts: IOptions<NLoopOptions>,
                     lightningClientProvider: ILightningClientProvider)  =
  inherit BackgroundService()

  override this.ExecuteAsync(ct: CancellationToken) = unitTask {
    let clis: (_ * _ * _) seq =
      opts.Value.OnChainCrypto
      |> Seq.distinct
      |> Seq.map(fun x ->
        (opts.Value.GetRPCClient x, lightningClientProvider.GetClient(x), x))
    try
      while not <| ct.IsCancellationRequested do
        for rpcClient, lightningClient, cc in clis do
          let! chs = lightningClient.ListChannels()
          return failwith "todo"
    with
    | :? OperationCanceledException ->
      logger.LogInformation($"Stopping {nameof(AutoLoopManager)}...")
    | ex ->
      logger.LogError($"{ex}")
  }


