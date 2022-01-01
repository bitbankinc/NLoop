namespace NLoop.Server.SwapServerClient

open System
open FsToolkit.ErrorHandling
open System.Threading
open BoltzClient
open FSharp.Control.Tasks
open System.Threading.Tasks
open System.Runtime.CompilerServices
open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Domain
open NLoop.Server.DTOs


[<AutoOpen>]
module private BoltzClientExtensionsHelpers =
  let inline percentToSat (amount: Money, percent: double) =
    (amount.Satoshi * (percent / 100. |> int64)) |> Money.Satoshis

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
    return {
      SwapFee =
        // boltz fee is returned with percentage, we have to convert to absolute value.
        percentToSat(req.Amount, p.Fees.Percentage)
      SweepMinerFee =
        p.Fees.MinerFees.BaseAsset.Reverse.Claim |> Money.Satoshis
      SwapPaymentDest =
        nodes.Nodes |> Seq.head |> fun i -> i.Value.NodeKey
      CltvDelta = timeoutResponse.Timeouts.[ps].Quote |> uint |> BlockHeightOffset32
      PrepayAmount =
        // In boltz, what we have to pay as `prepay.minerfee` always equals to their (estimated) lockup tx fee
        p.Fees.MinerFees.BaseAsset.Reverse.Lockup |> Money.Satoshis
    }
  }

  [<Extension>]
  static member GetLoopInQuote(this: BoltzClient, req: SwapDTO.LoopInQuoteRequest, ?ct: CancellationToken): Task<SwapDTO.LoopInQuote> = task {
    let ct = defaultArg ct CancellationToken.None
    let! r = this.GetPairsAsync(ct)
    let ps = PairId.toString(&req.Pair)
    let p = r.Pairs.[ps]
    return {
      SwapDTO.LoopInQuote.MinerFee = p.Fees.MinerFees.QuoteAsset.Normal |> Money.Satoshis
      SwapFee =
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

type BoltzSwapServerClient(b: BoltzClient) =
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
      let ct = defaultArg ct CancellationToken.None
      b.GetLoopInQuote(request, ct)
    member this.GetLoopOutQuote(request, ct) =
      let ct = defaultArg ct CancellationToken.None
      b.GetLoopOutQuote(request, ct)

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

    member this.ListenToSwapTx(swapId, ct) =
      let ct = defaultArg ct CancellationToken.None
      task {
        let mutable result = null
        while result |> isNull && not <| ct.IsCancellationRequested do
          let! resp = b.GetSwapStatusAsync(swapId.Value, ct).ConfigureAwait(false)
          match resp.Transaction with
          | Some {Tx = tx} ->
             result <- tx
          | None ->
            ()
          do! Task.Delay 6000
        if result |> isNull then
          failwith "unreachable: result is null"
        return result
      }

