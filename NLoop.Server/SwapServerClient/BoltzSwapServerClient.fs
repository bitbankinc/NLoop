namespace NLoop.Server.SwapServerClient

open FsToolkit.ErrorHandling
open System.Threading
open BoltzClient
open FSharp.Control.Tasks
open System.Threading.Tasks
open System.Runtime.CompilerServices
open DotNetLightning.Utils.Primitives
open LndClient
open Microsoft.Extensions.Options
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.DTOs
open FSharp.Control


[<AutoOpen>]
module private BoltzClientExtensionsHelpers =
  let inline percentToSat (amount: Money, percent: double) =
    (amount.Satoshi |> double) * (percent / 100.) |> int64 |> Money.Satoshis

/// Extensions to treat boltz client in the same way with the lightning loop
[<AbstractClass;Sealed;Extension>]
type BoltzClientExtensions =

  [<Extension>]
  static member GetLoopOutQuote(this: BoltzClient, req: SwapDTO.LoopOutQuoteRequest, ?ct: CancellationToken): Task<SwapDTO.LoopOutQuote> = task {
    let ct = defaultArg ct CancellationToken.None
    let! r = this.GetPairsAsync(ct)
    let ps = PairId.toString(&req.Pair)
    let p = r.Pairs.[ps]
    let! nodes = this.GetNodesAsync(ct)
    let! timeoutResponse = this.GetTimeoutsAsync(ct)
    let hasPrepay = r.Info |> Array.contains("prepay.minerfee")
    let minerFee = p.Fees.MinerFees.BaseAsset.Reverse.Lockup |> Money.Satoshis
    return {
      SwapDTO.LoopOutQuote.SwapFee =
        // boltz fee is returned with percentage, we have to convert to absolute value.
        percentToSat(req.Amount, p.Fees.Percentage)
        + if hasPrepay then Money.Zero else minerFee
      SwapDTO.LoopOutQuote.SweepMinerFee =
        p.Fees.MinerFees.BaseAsset.Reverse.Claim |> Money.Satoshis
      SwapDTO.LoopOutQuote.SwapPaymentDest =
        nodes.Nodes |> Seq.head |> fun i -> i.Value.NodeKey
      SwapDTO.LoopOutQuote.CltvDelta =
        timeoutResponse.Timeouts.[ps].Quote |> uint |> BlockHeightOffset32
      SwapDTO.LoopOutQuote.PrepayAmount =
        // In boltz, what we have to pay as `prepay.minerfee` always equals to their (estimated) lockup tx fee
        if hasPrepay then
          minerFee
        else
          Money.Zero
    }
  }

  [<Extension>]
  static member GetLoopInQuote(this: BoltzClient, req: SwapDTO.LoopInQuoteRequest, ?ct: CancellationToken): Task<SwapDTO.LoopInQuote> = task {
    let ct = defaultArg ct CancellationToken.None
    let! r = this.GetPairsAsync(ct)
    let ps = PairId.toString(&req.Pair)
    let p = r.Pairs.[ps]
    return {
      SwapDTO.LoopInQuote.MinerFee =
        p.Fees.MinerFees.QuoteAsset.Normal |> Money.Satoshis
      SwapDTO.LoopInQuote.SwapFee =
        percentToSat(req.Amount, p.Fees.Percentage)
    }
  }

  [<Extension>]
  static member GetLoopOutTerms(this: BoltzClient, pairId: PairId, zeroConf: bool, ?ct: CancellationToken) = task {
    let ct = defaultArg ct CancellationToken.None
    let! getPairsResponse = this.GetPairsAsync(ct)
    let ps = PairId.toString(&pairId)
    let p = getPairsResponse.Pairs.[ps]
    return {
      SwapDTO.OutTermsResponse.MaxSwapAmount =
        if zeroConf then p.Limits.MaximalZeroConf.BaseAsset else p.Limits.Maximal
        |> Money.Satoshis
      SwapDTO.MinSwapAmount =
        p.Limits.Minimal |> Money.Satoshis
    }
  }

  [<Extension>]
  static member GetLoopInTerms(this: BoltzClient, pairId: PairId, zeroConf, ?ct: CancellationToken) = task {
    let ct = defaultArg ct CancellationToken.None
    let! getPairsResponse = this.GetPairsAsync(ct)
    let ps = PairId.toString(&pairId)
    let p = getPairsResponse.Pairs.[ps]
    return {
      SwapDTO.InTermsResponse.MaxSwapAmount =
        if zeroConf then p.Limits.MaximalZeroConf.BaseAsset else p.Limits.Maximal
        |> Money.Satoshis
      SwapDTO.InTermsResponse.MinSwapAmount = p.Limits.Minimal |> Money.Satoshis
    }
  }

type BoltzSwapServerClient(b: BoltzClient, getWallet: GetWalletClient, getOptions: GetOptions, feeEst: IFeeEstimator) =
  interface ISwapServerClient with
    member this.LoopOut(request, ct) =
      let ct = defaultArg ct CancellationToken.None
      let r = {
        CreateReverseSwapRequest.InvoiceAmount = request.InvoiceAmount
        PairId = request.PairId
        OrderSide = OrderType.buy
        ClaimPublicKey = request.ClaimPublicKey
        PreimageHash = request.PreimageHash
      }
      b.CreateReverseSwapAsync(r, ct)
      |> Task.map(fun resp -> {
        SwapDTO.LoopOutResponse.Id = resp.Id
        LockupAddress = resp.LockupAddress
        Invoice = resp.Invoice
        TimeoutBlockHeight = resp.TimeoutBlockHeight
        OnchainAmount = resp.OnchainAmount
        RedeemScript = resp.RedeemScript
        MinerFeeInvoice = resp.MinerFeeInvoice
      })

    member this.LoopIn(request, ct) =
      let ct = defaultArg ct CancellationToken.None
      let req = {
        CreateSwapRequest.Invoice = request.Invoice
        PairId = request.PairId
        OrderSide = OrderType.buy
        RefundPublicKey = request.RefundPublicKey
      }
      b.CreateSwapAsync(req, ct)
      |> Task.map(fun resp -> {
        SwapDTO.LoopInResponse.Id = resp.Id
        Address = resp.Address
        RedeemScript = resp.RedeemScript
        AcceptZeroConf = resp.AcceptZeroConf
        ExpectedAmount = resp.ExpectedAmount
        TimeoutBlockHeight = resp.TimeoutBlockHeight
      })

    member this.GetLoopInQuote(request, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let! terms = b.GetLoopInTerms(request.Pair, false, ct)
        if request.Amount < terms.MinSwapAmount then
          let msg =
            "The loopin amount was too small that server does not accept our swap. " +
            $"(amount: {request.Amount.Satoshi} sats. Server minimum: {terms.MinSwapAmount.Satoshi} sats)"
          return Error msg
        else if terms.MaxSwapAmount < request.Amount then
          let msg =
            "The loopin amount was too large that server does not accept our swap. " +
            $"(amount: {request.Amount.Satoshi} sats. Server maximum: {terms.MaxSwapAmount.Satoshi} sats)"
          return Error msg
        else
          let! fee =
            let onChainAsset = request.Pair.Quote
            let dummyP2SHAddr =
              let n = getOptions().GetNetwork(onChainAsset)
              Scripts.dummySwapScriptV1.WitHash.GetAddress(n)
            let wallet = getWallet(onChainAsset)
            let dest = seq [(dummyP2SHAddr, request.Amount)] |> dict
            wallet.GetSendingTxFee(dest, request.HtlcConfTarget, ct)
          match fee with
          | Error e -> return Error <| e.ToString()
          | Ok f ->
            let! q = b.GetLoopInQuote(request, ct)
            // we want to use miner fee estimated by ourselves, since 1. it is target-blocknum aware and 2. we have no
            // reason to trust the conuterparty.
            return Ok { q with MinerFee = f }
      }
    member this.GetLoopOutQuote(request, ct) =
      task {
        let ct = defaultArg ct CancellationToken.None
        let! terms = b.GetLoopOutTerms(request.Pair, false, ct)
        if request.Amount < terms.MinSwapAmount then
          let msg =
            "The loopout amount was too small that server does not accept our swap. " +
            $"(amount: {request.Amount.Satoshi} sats. Server minimum: {terms.MinSwapAmount.Satoshi} sats)"
          return Error msg
        else if terms.MaxSwapAmount < request.Amount then
          let msg =
            "The loopout amount was too large that server does not accept our swap. " +
            $"(amount: {request.Amount.Satoshi} sats. Server maximum: {terms.MaxSwapAmount.Satoshi} sats)"
          return Error msg
        else
          let onChainAsset = request.Pair.Base
          let! feeRate = feeEst.Estimate(request.SweepConfTarget) (onChainAsset)
          let minerFee =
            Transactions.dummyClaimTxFee feeRate
          let! quote = b.GetLoopOutQuote(request, ct)
          // we want to use miner fee estimated by ourselves, since 1. it is target-blocknum aware and 2. we have no
          // reason to trust the conuterparty.
          return Ok { quote with SweepMinerFee = minerFee }
      }

    member this.GetNodes(ct) =
      let ct = defaultArg ct CancellationToken.None
      b.GetNodesAsync(ct)
      |> Task.map(fun resp -> {
        SwapDTO.GetNodesResponse.Nodes =
          resp.Nodes
          |> Map.map(fun _ v -> {
            SwapDTO.NodeInfo.Uris = v.Uris
            SwapDTO.NodeInfo.NodeKey = v.NodeKey
          })
      })

    member this.GetLoopInTerms({ SwapDTO.InTermsRequest.PairId = pairId; IsZeroConf = zeroConf }, ct) =
      let ct = defaultArg ct CancellationToken.None
      b.GetLoopInTerms(pairId, zeroConf, ct)
      |> Task.map(fun resp -> {
        SwapDTO.InTermsResponse.MaxSwapAmount = resp.MaxSwapAmount
        SwapDTO.InTermsResponse.MinSwapAmount = resp.MinSwapAmount
      })

    member this.GetLoopOutTerms({ SwapDTO.OutTermsRequest.PairId = pairId; IsZeroConf = zeroConf }, ct) =
      let ct = defaultArg ct CancellationToken.None
      b.GetLoopOutTerms(pairId, zeroConf, ct)
      |> Task.map(fun resp -> {
        SwapDTO.OutTermsResponse.MaxSwapAmount = resp.MaxSwapAmount
        SwapDTO.OutTermsResponse.MinSwapAmount = resp.MinSwapAmount
      })

    member this.CheckConnection(ct) =
      let ct = defaultArg ct CancellationToken.None
      b.GetVersionAsync(ct) :> Task

    member this.ListenToSwapTx(swapId, onSwapTx , ct) =
      let ct = defaultArg ct CancellationToken.None
      asyncSeq {
        for resp in b.StartListenToSwapStatusChange(swapId.Value) do
          match resp.Transaction with
          | Some {Tx = tx} when resp.SwapStatus = SwapStatusType.TxMempool ->
            do! onSwapTx tx |> Async.AwaitTask
            return ()
          | _ ->
            ()
      }
      |> AsyncSeq.tryFirst
      |> Async.map(function | None -> printfn "Unreachable!"; failwith "Unreachable!" | Some a -> a)
      |> fun a -> Async.StartAsTask(a, TaskCreationOptions.None, ct) :> Task

