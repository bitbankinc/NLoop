namespace NLoop.Server.Handlers

open System.Collections.Generic
open System.Threading.Tasks
open DotNetLightning.Utils.Primitives
open DotNetLightning.Utils
open Microsoft.Extensions.Logging
open NBitcoin
open NLoop.Domain
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Server.DTOs
open NLoop.Server.Options
open NLoop.Server.SwapServerClient

/// An error agnostic to API (or transport) layer.
/// This must be translated into either 1. JsonRpc 2.0 error code or 2. HTTP status code
/// depending on how the server has been run.
[<RequireQualifiedAccess>]
type HandlerError =
  /// User has sent invalid request.
  | InvalidRequest of string list
  /// Error that might be resolved by waiting
  /// (e.g. we can not reach swap server, blockchain is not synced. etc.)
  | ServiceUnAvailable of string
  /// Other error occured in the server.
  | InternalError of string
  with
  static member FromEventSourcingError(e: EventSourcingError<_>) =
    e.ToString() |> InternalError
    
  member this.Message =
    match this with
    | HandlerError.InvalidRequest e -> String.concat "; " e
    | HandlerError.ServiceUnAvailable e -> e
    | InternalError e -> e
  
[<AutoOpen>]
module HandlerHelpers =
  let internal checkBlockHeightIsSyncAndGetTip(getClient: GetBlockchainClient) (cryptoCodePair: PairId) =
    task {
      let ccs =
        let struct (ourCryptoCode, theirCryptoCode) = cryptoCodePair.Value
        [ourCryptoCode; theirCryptoCode] |> Seq.distinct
      let mutable errorMsg = null
      
      let blockHeights = Dictionary<SupportedCryptoCode, BlockHeight>()
      for cc in ccs do
        let rpcClient = getClient(cc)
        let! info = rpcClient.GetBlockChainInfo()
        blockHeights.Add(cc, info.Height.Value |> BlockHeight)
        if info.Progress < 0.99999f then
          errorMsg <- $"{cc} blockchain is not synced. VerificationProgress: %f{info.Progress}. Please wait until its done."
        else
          ()

      if (errorMsg |> isNull) then
        return Ok blockHeights
      else
        return Error(HandlerError.ServiceUnAvailable errorMsg)
    }
  
  let internal checkWeHaveChannel
    (offChainCryptoCode: SupportedCryptoCode)
    (chanIds: ShortChannelId seq)
    (lnClientProvider: ILightningClientProvider) =
    task {
      let cli = lnClientProvider.GetClient(offChainCryptoCode)
      let! channels = cli.ListChannels()
      let nonExistentChannel = ResizeArray()
      for chanId in chanIds do
        if channels |> Seq.exists(fun actualChannel -> actualChannel.Id = chanId) |> not then
          nonExistentChannel.Add(chanId)

      if nonExistentChannel.Count > 0 then
        return
            (nonExistentChannel |> Seq.map(fun cId -> $"Channel {cId.ToUserFriendlyString()} does not exist")) |> Seq.toList
            |> HandlerError.InvalidRequest
            |> Error
      else
        return Ok()
    }
    
  let internal checkWeHaveRouteToCounterParty
    (offChainCryptoCode: SupportedCryptoCode)
    (amt: Money)
    (chanIdsSpecified: ShortChannelId[])
    (lnClientProvider: ILightningClientProvider)
    (boltzCli: ISwapServerClient)
    (logger: ILogger)
    =
    task {
      let cli = lnClientProvider.GetClient(offChainCryptoCode)
      let! nodes = boltzCli.GetNodes()
      match nodes.Nodes |> Seq.tryFind(fun kv -> kv.Key = offChainCryptoCode.ToString()) with
      | None ->
          return
            [$"counterparty server does not support {offChainCryptoCode.ToString()} as an off-chain currency"]
            |> HandlerError.InvalidRequest
            |> Error
      | Some kv ->
        if chanIdsSpecified.Length = 0 then
          let! r = cli.QueryRoutes(kv.Value.NodeKey, amt.ToLNMoney())
          if (r.Value.Length > 0) then
            let chanId = r.Value.Head.ShortChannelId
            logger.LogDebug("paying through the channel {ChannelId})", chanId.ToUserFriendlyString())
            return Ok()
          else
            return
              "Failed to find route to Boltz server. Make sure you have open and active channel"
              |> HandlerError.ServiceUnAvailable
              |> Error
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
            return Ok()
          else
            let msg = $"Failed to find route to Boltz server. Make sure the channels you specified is open and active"
            return msg |> HandlerError.ServiceUnAvailable |> Error
    }
    
  let internal validateFeeLimitAgainstServerQuote
    (swapServerClient: ISwapServerClient)
    (req: LoopOutRequest) =
    task {
      let! quote =
        let r = { SwapDTO.LoopOutQuoteRequest.Amount = req.Amount
                  SwapDTO.SweepConfTarget =
                    req.SweepConfTarget
                    |> ValueOption.map (uint32 >> BlockHeightOffset32)
                    |> ValueOption.defaultValue req.PairIdValue.DefaultLoopOutParameters.SweepConfTarget
                  SwapDTO.Pair = req.PairIdValue }
        swapServerClient.GetLoopOutQuote(r)

      match quote with
      | Error s ->
        return s |> HandlerError.InternalError |> Error
      | Ok quote ->
        return
          quote.Validate(req.Limits)
          |> Result.mapError(fun e -> [e.Message] |> HandlerError.InvalidRequest)
  }

  let internal validateLoopInFeeLimitAgainstServerQuote
    (swapServerClient: ISwapServerClient)
    (req: LoopInRequest) =
    task {
      let pairId =
        req.PairId
        |> Option.defaultValue PairId.Default
      let! quote =
        let r = {
          SwapDTO.LoopInQuoteRequest.Amount = req.Amount
          SwapDTO.LoopInQuoteRequest.Pair = pairId
          SwapDTO.LoopInQuoteRequest.HtlcConfTarget =
            req.HtlcConfTarget
            |> ValueOption.map(uint32 >> BlockHeightOffset32)
            |> ValueOption.defaultValue(pairId.DefaultLoopInParameters.HTLCConfTarget)
        }
        swapServerClient.GetLoopInQuote(r)
      match quote with
      | Error s ->
        return s |> HandlerError.InternalError |> Error
      | Ok quote ->
        return
          quote.Validate(req.Limits)
          |> Result.mapError(fun e -> [e.Message] |> HandlerError.InvalidRequest)
  }
