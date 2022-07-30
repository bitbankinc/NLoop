module NLoop.Server.Handlers.LoopHandlers

open System
open System.Linq
open System.Threading.Tasks
open DotNetLightning.Utils.Primitives
open FSharp.Control.Reactive

open FsToolkit.ErrorHandling
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.Crypto
open NLoop.Domain
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Server.Actors
open NLoop.Server.DTOs
open NLoop.Server.Handlers
open NLoop.Server.Services
open System.Reactive.Linq

open DotNetLightning.Utils

open Microsoft.AspNetCore.Http
open FSharp.Control.Tasks
open Giraffe

let private validateLoopOutRequest (opts: NLoopOptions) (req: LoopOutRequest) =
  req.Validate(opts.GetNetwork) |> Result.mapError(HandlerError.InvalidRequest)

let handleLoopOut
  (getOpts: GetOptions)
  (getClient: GetBlockchainClient)
  (lnClientProvider: ILightningClientProvider)
  (swapServerClient)
  (swapExecutor: ISwapExecutor)
  (logger)
  (req: LoopOutRequest) =
  taskResult {
    let opts = getOpts()
    do! validateLoopOutRequest opts req
    let! heights = checkBlockHeightIsSyncAndGetTip getClient req.PairIdValue
    do! checkWeHaveChannel(req.PairIdValue.Quote) req.OutgoingChannelIds lnClientProvider
    do!
      checkWeHaveRouteToCounterParty
        req.PairIdValue.Quote
        req.Amount
        req.OutgoingChannelIds
        lnClientProvider
        swapServerClient
        logger
    do! validateFeeLimitAgainstServerQuote swapServerClient req
    return!
      swapExecutor.ExecNewLoopOut(req, heights.[req.PairIdValue.Base])
      |> TaskResult.mapError HandlerError.InternalError
  }

let handleLoopIn
  getBlockchainClient
  swapServerClient
  (executor: ISwapExecutor)
  (loopIn: LoopInRequest) =
  taskResult {
    let! heights =
      checkBlockHeightIsSyncAndGetTip
        getBlockchainClient
        loopIn.PairIdValue
    do!
        validateLoopInFeeLimitAgainstServerQuote swapServerClient loopIn
    return!
      executor.ExecNewLoopIn(loopIn, heights.[loopIn.PairIdValue.Quote])
      |> TaskResult.mapError(HandlerError.InternalError)
  }