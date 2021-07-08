namespace NLoop.Server

open System.Text.Json
open BTCPayServer.Lightning.LND
open DotNetLightning.Utils
open Giraffe
open Microsoft.AspNetCore.Http
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Options
open Microsoft.Extensions.Logging
open NBitcoin
open NLoop.Domain
open NLoop.Server.DTOs
open NLoop.Server.Services

[<AutoOpen>]
module CustomHandlers =
  let bindJsonWithCryptoCode<'T> cryptoCode (f: SupportedCryptoCode -> 'T -> HttpHandler): HttpHandler =
      fun (next : HttpFunc) (ctx : HttpContext) ->
          task {
              let errorResp() =
                ctx.SetStatusCode StatusCodes.Status400BadRequest
                ctx.WriteJsonAsync({|error = $"unsupported cryptocode {cryptoCode}" |})
              match SupportedCryptoCode.TryParse cryptoCode with
              | Some c ->
                match (ctx.GetService<IRepositoryProvider>().TryGetRepository c) with
                | Some repo ->
                  let! model =
                    JsonSerializer.DeserializeAsync<'T>(ctx.Request.Body, repo.JsonOpts)
                  return! f c model next ctx
                | None -> return! errorResp()
              | None -> return! errorResp()
          }

  type SSEEvent = {
    Name: string
    Data: obj
    Id: string
    Retry: int option
  }

  type HttpContext with
    member this.SetBlockHeight(cc, height: uint64) =
      this.Items.Add($"{cc}-BlockHeight", BlockHeight(uint32 height))
    member this.GetBlockHeight(cc) =
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
        let struct(ourCryptoCode, theirCryptoCode) = cryptoCodePair
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
      let mutable maybeResult = null
      for kv in nodes.Nodes do
        if (maybeResult |> isNull |> not) then () else
        try
          let! r  = (cli :?> LndClient).SwaggerClient.QueryRoutesAsync(kv.Value.NodeKey.ToHex(), amt.Satoshi.ToString(), 1)
          if (r.Routes.Count > 0) then
            maybeResult <- r
        with
        | ex ->
          ctx
            .GetLogger<_>()
            .LogError $"{ex}"
          ()

      if maybeResult |> isNull then
        return! error503 $"Failed to find route to Boltz server. Make sure the channel is open" next ctx
      else
        return! next ctx
    }
