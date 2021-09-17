namespace NLoop.Server.Services

open System
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils.Primitives
open Microsoft.Extensions.Hosting
open FSharp.Control.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.DTOs
open NLoop.Server.Projections

[<RequireQualifiedAccess>]
type SwapDisqualifiedReason =
  | None

type SwapSuggestion = {
  OutSwaps: LoopOutRequest []
  DisqualifiedChannels: Map<ShortChannelId, SwapDisqualifiedReason>
  DisqualifiedPeers: Map<NodeId, SwapDisqualifiedReason>
}
  with
  static member Zero = {
    OutSwaps = [||]
    DisqualifiedChannels = Map.empty
    DisqualifiedPeers = Map.empty
  }

type private ExistingAutoLoopSummary = {
  SpentFees: Money
  PendingFees: Money
  InFlightCount: int
}

type AutoLoopManager(logger: ILogger<AutoLoopManager>,
                     opts: IOptions<NLoopOptions>,
                     projection: SwapStateProjection,
                     lightningClientProvider: ILightningClientProvider)  =
 inherit BackgroundService()

  member private this.CheckExistingAutoLoopsIn(loopIn: LoopIn) =
    ()

  member private this.CheckExistingAutoLoopsOut(loopOuts: LoopOut[]): ExistingAutoLoopSummary =
    for o in loopOuts do
      if o.Label <> Labels.autoLoopLabel(Swap.Category.Out) then
        ()
      else
        if o.Status = SwapStatusType.InvoicePending then
          ()
      let prepay = o.Invoice
      ()
    {
      SpentFees = failwith "todo"
      PendingFees = failwith "todo"
      InFlightCount = loopOuts.Length
    }

  member this.SuggestSwaps(category: Swap.Category): Task<SwapSuggestion> = task {
    match category with
    | Swap.Category.In ->
      let loopIns = projection.OngoingLoopIns
      ()
    | Swap.Category.Out ->
      let loopOuts = projection.OngoingLoopOuts
      ()

    return failwith "todo"
  }

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


