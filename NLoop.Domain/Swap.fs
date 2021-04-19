namespace NLoop.Domain

open System.Linq
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open NBitcoin
open NLoop.Server
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module Swap =
  // ------ state -----
  type SwapList = {
    Out: LoopOut seq
    In: LoopIn seq
  }
  type State = {
    OnGoing: SwapList
    Logger: ILogger
    Broadcaster: IBroadcaster
    FeeEstimator: IFeeEstimator
  }
    with
    static member Create(l, b, f) = {
      State.OnGoing = { Out = []; In = [] }
      Logger = l
      Broadcaster = b
      FeeEstimator = f
    }

  // ------ command -----

  module Data =
    type TxInfo = {
      TxId: uint256
      Tx: Transaction
      Eta: int
    }
    and SwapStatusResponseData = {
      _Status: string
      Transaction: TxInfo option
      FailureReason: string option
    }
      with
      member this.SwapStatus =
        match this._Status with
        | "swap.created" -> SwapStatusType.Created
        | "invoice.set" -> SwapStatusType.InvoiceSet
        | "transaction.mempool" -> SwapStatusType.TxMempool
        | "transaction.confirmed" -> SwapStatusType.TxConfirmed
        | "invoice.payed" -> SwapStatusType.InvoicePayed
        | "invoice.failedToPay" -> SwapStatusType.InvoiceFailedToPay
        | "transaction.claimed" -> SwapStatusType.TxClaimed
        | x -> SwapStatusType.Unknown

    and SwapStatusUpdate = {
      Id: string
      Response: SwapStatusResponseData
      Network: Network
    }

  type Command =
    | NewLoopOut of LoopOut
    | NewLoopIn of LoopIn
    | SwapUpdate of Data.SwapStatusUpdate

  // ------ event -----

  type Event =
    | ClaimTxPublished of txid: uint256 * swapId: string

  // ------ error -----
  type Error =
    | NoOp

  let executeCommand (s: State) (command: Command): Task<Result<Event list, Error>> =
    taskResult {
      match command with
      | SwapUpdate u when s.OnGoing.Out |> Seq.exists(fun o -> u.Id = o.Id) ->
        let ourSwap = s.OnGoing.Out.First(fun o -> u.Id = o.Id)
        if (u.Response.SwapStatus = ourSwap.Status) then
          s.Logger.LogDebug($"Swap Status for id {ourSwap.Id} update is not new for us.")
          return []
        else
        match u.Response.SwapStatus with
        | SwapStatusType.TxMempool when not <| ourSwap.AcceptZeroConf ->
          return []
        | SwapStatusType.TxMempool
        | SwapStatusType.TxConfirmed ->
          let (ourCryptoCode, counterPartyCryptoCode) = ourSwap.PairId
          let! feeRate = s.FeeEstimator.Estimate(counterPartyCryptoCode)
          let lockupTx =
            u.Response.Transaction.Value.Tx // In case of TxConfirmed or TxMempool, it must always have a tx.
          let claimTx =
            Transactions.createClaimTx
              (BitcoinAddress.Create(ourSwap.ClaimAddress, u.Network))
              (ourSwap.PrivateKey)
              (ourSwap.Preimage)
              (ourSwap.RedeemScript)
              (feeRate)
              (lockupTx)
              (u.Network)
          do! s.Broadcaster.BroadcastTx(claimTx, ourCryptoCode)
          let txid = claimTx.GetWitHash()
          return [ClaimTxPublished(txid, ourSwap.Id)]
        | swapStatus ->
          s.Logger.LogWarning($"Unexpected Swap Status {swapStatus}")
          return []
      | SwapUpdate u when s.OnGoing.In |> Seq.exists(fun o -> u.Id = o.Id) ->
        let ourSwap = s.OnGoing.In.First(fun o -> u.Id = o.Id)
        return []
      | SwapUpdate u ->
        s.Logger.LogWarning($"unknown swap id {u.Id}")
        return []
    }

  let applyChanges (state: State) (event: Event) =
    match event with
    | ClaimTxPublished (txid, id) ->
      let newOuts =
        state.OnGoing.Out |> Seq.map(fun x -> if x.Id = id then { x with ClaimTransactionId = Some txid } else x)
      { state with OnGoing = { state.OnGoing with Out = newOuts } }

  type Aggregate = Aggregate<State, Command, Event, Error>
