namespace NLoop.Server

open FsToolkit.ErrorHandling
open System.Text.Json
open DotNetLightning.Utils
open Giraffe
open LndClient
open Microsoft.AspNetCore.Http
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Options
open Microsoft.Extensions.Logging
open NBitcoin
open NLoop.Domain
open NLoop.Server.DTOs
open NLoop.Server.SwapServerClient

[<AutoOpen>]
module CustomHandlers =
  type SSEEvent = {
    Name: string
    Data: obj
    Id: string
    Retry: int option
  }

  type HttpContext with
    member this.SetBlockHeight(cc, height: uint64) =
      this.Items.Add($"{cc}-BlockHeight", BlockHeight(uint32 height))
    member this.GetBlockHeight(cc: SupportedCryptoCode) =
      match this.Items.TryGetValue($"{cc}-BlockHeight") with
      | false, _ -> failwithf "Unreachable! could not get block height for %A" cc
      | true, v -> v :?> BlockHeight

  let inline internal error503 e =
    setStatusCode StatusCodes.Status503ServiceUnavailable
      >=> json {| error = e.ToString() |}

  let inline internal validationError400 (errors: #seq<string>) =
    setStatusCode StatusCodes.Status400BadRequest
      >=> json {| errors = errors |}

  let internal checkBlockchainIsSyncedAndSetTipHeight(cryptoCodePair: PairId) =
    fun (next : HttpFunc) (ctx : HttpContext) -> task {

      let opts = ctx.GetService<IOptions<NLoopOptions>>()
      let ccs =
        let struct (ourCryptoCode, theirCryptoCode) = cryptoCodePair.Value
        [ourCryptoCode; theirCryptoCode] |> Seq.distinct
      let mutable errorMsg = null
      for cc in ccs do
        let rpcClient = opts.Value.GetRPCClient(cc)
        let! info = rpcClient.GetBlockchainInfoAsync()
        ctx.SetBlockHeight(cc, info.Blocks)
        if info.VerificationProgress < 1.f then
          errorMsg <- $"{cc} blockchain is not synced. VerificationProgress: %f{info.VerificationProgress}"
        else
          ()

      if (errorMsg |> isNull) then
        return! next ctx
      else
        return! error503 errorMsg next ctx
    }

  open System.Threading.Tasks
  let internal checkWeHaveRouteToCounterParty(offChainCryptoCode: SupportedCryptoCode) (amt: Money) =
    fun (next: HttpFunc) ( ctx: HttpContext) -> task {
      let cli = ctx.GetService<ILightningClientProvider>().GetClient(offChainCryptoCode)
      let boltzCli = ctx.GetService<BoltzClient>()
      let nodesT = boltzCli.GetNodesAsync()
      let! nodes = nodesT
      let mutable maybeResult = None
      let logger =
        ctx
          .GetLogger<_>()
      for kv in nodes.Nodes do
        if (maybeResult.IsSome) then () else
        try
          let! r  = cli.QueryRoutes(kv.Value.NodeKey, amt.ToLNMoney())
          if (r.Value.Length > 0) then
            maybeResult <- Some r
        with
        | ex ->
          logger
            .LogError $"{ex}"

      if maybeResult.IsNone then
        return! error503 $"Failed to find route to Boltz server. Make sure the channel is open" next ctx
      else
        let chanIds =
          maybeResult.Value.Value
          |> List.head
          |> fun firstRoute -> firstRoute.ShortChannelId
        logger.LogDebug($"paying through following channel ({chanIds})")
        return! next ctx
    }

  let internal validateFeeLimitAgainstServerQuote(req: LoopOutRequest) =
    fun (next : HttpFunc) (ctx : HttpContext) -> task {
      let boltzClient = ctx.GetService<BoltzClient>()
      let! quote =
        let r = { LoopOutQuoteRequest.Amount = req.Amount
                  SweepConfTarget =
                    req.SweepConfTarget
                    |> ValueOption.map (uint32 >> BlockHeightOffset32)
                    |> ValueOption.defaultValue(req.PairIdValue.DefaultLoopOutParameters.SweepConfTarget)
                  Pair = req.PairIdValue }
        boltzClient.GetLoopOutQuote(r)
      let r =
        quote.Validate(req.Limits)
        |> Result.mapError(fun e -> e.Message)
      match r with
      | Error e -> return! validationError400 [e] next ctx
      | Ok () -> return! next ctx
  }

  let internal validateLoopInFeeLimitAgainstServerQuote(req: LoopInRequest) =
    fun (next : HttpFunc) (ctx : HttpContext) -> task {
      let pairId =
        req.PairId
        |> Option.defaultValue PairId.Default
      let boltzClient = ctx.GetService<BoltzClient>()
      let! quote =
        let r = { LoopInQuoteRequest.Amount = req.Amount; Pair = pairId }
        boltzClient.GetLoopInQuote(r)
      let r = quote.Validate(req.Limits) |> Result.mapError(fun e -> e.Message)
      match r with
      | Error e -> return! validationError400 [e] next ctx
      | Ok () -> return! next ctx
  }
