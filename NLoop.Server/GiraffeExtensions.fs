namespace NLoop.Server

open System.Threading.Tasks
open FsToolkit.ErrorHandling
open DotNetLightning.Utils
open Giraffe
open Microsoft.AspNetCore.Http
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Logging
open NBitcoin
open NLoop.Domain
open NLoop.Server.Options
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

  let inline internal errorBadRequest (errors: #seq<string>) =
    setStatusCode StatusCodes.Status400BadRequest
      >=> json {| errors = errors |}

  let internal checkBlockchainIsSyncedAndSetTipHeight(cryptoCodePair: PairId) =
    fun (next : HttpFunc) (ctx : HttpContext) -> task {

      let getClient = ctx.GetService<GetBlockchainClient>()
      let ccs =
        let struct (ourCryptoCode, theirCryptoCode) = cryptoCodePair.Value
        [ourCryptoCode; theirCryptoCode] |> Seq.distinct
      let mutable errorMsg = null
      for cc in ccs do
        let rpcClient = getClient(cc)
        let! info = rpcClient.GetBlockChainInfo()
        ctx.SetBlockHeight(cc, info.Height.Value |> uint64)
        if info.Progress < 0.99999f then
          errorMsg <- $"{cc} blockchain is not synced. VerificationProgress: %f{info.Progress}. Please wait until its done."
        else
          ()

      if (errorMsg |> isNull) then
        return! next ctx
      else
        return! error503 errorMsg next ctx
    }

  let internal checkWeHaveChannel (offChainCryptoCode: SupportedCryptoCode) (chanIds: ShortChannelId seq) =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
      let cli = ctx.GetService<ILightningClientProvider>().GetClient(offChainCryptoCode)
      let! channels = cli.ListChannels()
      let nonExistentChannel = ResizeArray()
      for chanId in chanIds do
        if channels |> Seq.exists(fun actualChannel -> actualChannel.Id = chanId) |> not then
          nonExistentChannel.Add(chanId)

      if nonExistentChannel.Count > 0 then
        return!
          errorBadRequest
            (nonExistentChannel |> Seq.map(fun cId -> $"Channel {cId.ToUserFriendlyString()} does not exist")) next ctx
      else
        return! next ctx
    }

  let internal checkWeHaveRouteToCounterParty(offChainCryptoCode: SupportedCryptoCode) (amt: Money) (chanIdsSpecified: ShortChannelId[]) =
    fun (next: HttpFunc) ( ctx: HttpContext) -> task {
      let cli = ctx.GetService<ILightningClientProvider>().GetClient(offChainCryptoCode)
      let boltzCli = ctx.GetService<ISwapServerClient>()
      let! nodes = boltzCli.GetNodes()
      let logger =
        ctx
          .GetLogger<_>()
      match nodes.Nodes |> Seq.tryFind(fun kv -> kv.Key = offChainCryptoCode.ToString()) with
      | None ->
          return! errorBadRequest [$"counterparty server does not support {offChainCryptoCode.ToString()} as an off-chain currency"] next ctx
      | Some kv ->
        if chanIdsSpecified.Length = 0 then
          let! r = cli.QueryRoutes(kv.Value.NodeKey, amt.ToLNMoney())
          if (r.Value.Length > 0) then
            let chanId = r.Value.Head.ShortChannelId
            logger.LogDebug("paying through the channel {ChannelId})", chanId.ToUserFriendlyString())
            return! next ctx
          else
            return! error503 "Failed to find route to Boltz server. Make sure you have open and active channel" next ctx
        else
          let! routes =
            chanIdsSpecified
            |> Array.map(fun chanId ->
              cli.QueryRoutes(kv.Value.NodeKey, amt.ToLNMoney(), chanId)
            )
            |> Task.WhenAll
          let foundRoutes = routes |> Array.filter(fun r -> r.Value.Length > 0)
          if foundRoutes.Length > 0 then
            let chanIds =
              foundRoutes
              |> Array.map(fun r -> r.Value.Head.ShortChannelId.ToUserFriendlyString())
              |> Array.toList
            logger.LogDebug("paying through channels {ChannelIds})", chanIds)
            return! next ctx
          else
            let msg = $"Failed to find route to Boltz server. Make sure the channels you specified is open and active"
            return! error503 msg next ctx
    }

  let internal validateFeeLimitAgainstServerQuote(req: LoopOutRequest) =
    fun (next : HttpFunc) (ctx : HttpContext) -> task {
      let swapServerClient = ctx.GetService<ISwapServerClient>()
      let! quote =
        let r = { SwapDTO.LoopOutQuoteRequest.Amount = req.Amount
                  SwapDTO.SweepConfTarget =
                    req.SweepConfTarget
                    |> ValueOption.map (uint32 >> BlockHeightOffset32)
                    |> ValueOption.defaultValue req.PairIdValue.DefaultLoopOutParameters.SweepConfTarget
                  SwapDTO.Pair = req.PairIdValue }
        swapServerClient.GetLoopOutQuote(r)
      let r =
        quote.Validate(req.Limits)
        |> Result.mapError(fun e -> e.Message)
      match r with
      | Error e -> return! errorBadRequest [e] next ctx
      | Ok () -> return! next ctx
  }

  let internal validateLoopInFeeLimitAgainstServerQuote(req: LoopInRequest) =
    fun (next : HttpFunc) (ctx : HttpContext) -> task {
      let pairId =
        req.PairId
        |> Option.defaultValue PairId.Default
      let swapServerClient = ctx.GetService<ISwapServerClient>()
      let! quote =
        let r = {
          SwapDTO.LoopInQuoteRequest.Amount = req.Amount
          SwapDTO.LoopInQuoteRequest.Pair = pairId
        }
        swapServerClient.GetLoopInQuote(r)
      let r = quote.Validate(req.Limits) |> Result.mapError(fun e -> e.Message)
      match r with
      | Error e -> return! errorBadRequest [e] next ctx
      | Ok () -> return! next ctx
  }
