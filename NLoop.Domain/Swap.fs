namespace NLoop.Domain

open System
open System.Collections.Generic
open System.Linq
open System.Threading.Tasks
open DotNetLightning.Payment
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open FSharp.Control.Tasks

[<RequireQualifiedAccess>]
module Swap =
  // ------ state -----
  type SwapList = {
    Out: LoopOut list
    In: LoopIn list
  }

  type State = {
    OnGoing: SwapList
  }
    with
    static member Zero = {
      State.OnGoing = { Out = []; In = [] }
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
        | "swap.created" -> SwapStatusType.SwapCreated
        | "swap.expired" -> SwapStatusType.SwapExpired

        | "invoice.set" -> SwapStatusType.InvoiceSet
        | "invoice.payed" -> SwapStatusType.InvoicePayed
        | "invoice.pending" -> SwapStatusType.InvoicePending
        | "invoice.settled" -> SwapStatusType.InvoiceSettled
        | "invoice.failedToPay" -> SwapStatusType.InvoiceFailedToPay

        | "channel.created" -> SwapStatusType.ChannelCreated

        | "transaction.failed" -> SwapStatusType.TxFailed
        | "transaction.mempool" -> SwapStatusType.TxMempool
        | "transaction.claimed" -> SwapStatusType.TxClaimed
        | "transaction.refunded" -> SwapStatusType.TxRefunded
        | "transaction.confirmed" -> SwapStatusType.TxConfirmed
        | _ -> SwapStatusType.Unknown

    and SwapStatusUpdate = {
      Id: string
      Response: SwapStatusResponseData
      Network: Network
    }

  type Msg =
    | NewLoopOut of LoopOut
    | NewLoopIn of LoopIn
    | SwapUpdate of Data.SwapStatusUpdate
    | SetValidationError of id: string * err: string
    | OffChainOffer of AsyncOperationStatus<string>
    | NoOp

  // ------ event -----
  type Event =
    | KnownSwapAddedAgain of id: string
    | NewLoopOutAdded of LoopOut
    | NewLoopInAdded of LoopIn
    | LoopErrored of id: string * err: string
    | ClaimTxPublished of txid: uint256 * swapId: string
    | SwapTxPublished of txid: uint256 * swapId: string

  // ------ deps -----
  type Deps = {
    Broadcaster: IBroadcaster
    FeeEstimator: IFeeEstimator
    UTXOProvider: IUTXOProvider
    LightningClient: INLoopLightningClient
    GetChangeAddress: GetChangeAddress
  }

  // ----- aggregates ----

  let executeCommand
    { Broadcaster = broadcaster; FeeEstimator = feeEstimator; UTXOProvider = utxoProvider;
      GetChangeAddress = getChangeAddress; LightningClient = lnClient  }
    (s: State)
    (msg: Msg): Task<Event list> =
    task {
      match msg with
      | NewLoopOut loopOut when s.OnGoing.Out |> Seq.exists(fun o -> o.Id = loopOut.Id) ->
        return [KnownSwapAddedAgain loopOut.Id]
      | NewLoopOut loopOut ->
        return [NewLoopOutAdded loopOut]
      | NewLoopIn loopIn when s.OnGoing.In |> Seq.exists(fun o -> o.Id = loopIn.Id) ->
        return [KnownSwapAddedAgain loopIn.Id]
      | NewLoopIn loopIn ->
        return [NewLoopInAdded loopIn]
      | SwapUpdate u when s.OnGoing.Out |> Seq.exists(fun o -> u.Id = o.Id) ->
        let ourSwap = s.OnGoing.Out.First(fun o -> u.Id = o.Id)
        if (u.Response.SwapStatus = ourSwap.Status) then
          return []
        else
        match u.Response.SwapStatus with
        | SwapStatusType.TxMempool when not <| ourSwap.AcceptZeroConf ->
          return []
        | SwapStatusType.TxMempool
        | SwapStatusType.TxConfirmed ->
          let (ourCryptoCode, counterPartyCryptoCode) = ourSwap.PairId
          let! feeRate =
            feeEstimator.Estimate(counterPartyCryptoCode)
          let lockupTx =
            u.Response.Transaction |> Option.defaultWith(fun () -> raise <| Exception("No Transaction in response"))
          let claimTx =
            Transactions.createClaimTx
              (BitcoinAddress.Create(ourSwap.ClaimAddress, u.Network))
              (ourSwap.PrivateKey)
              (ourSwap.Preimage)
              (ourSwap.RedeemScript)
              (feeRate)
              (lockupTx.Tx)
              (u.Network)
            |> function | Ok t -> t | Error e -> raise <| Exception (sprintf "%A" e)
          do!
            broadcaster.BroadcastTx(claimTx, ourCryptoCode)
          let txid = claimTx.GetWitHash()
          return [ClaimTxPublished(txid, ourSwap.Id)]
        | _ ->
          return []
      | SwapUpdate u when s.OnGoing.In |> Seq.exists(fun o -> u.Id = o.Id) ->
        let ourSwap = s.OnGoing.In.First(fun o -> u.Id = o.Id)
        match u.Response.SwapStatus with
        | SwapStatusType.InvoiceSet ->
          // TODO: check confirmation?
          let (ourCryptoCode, counterPartyCryptoCode) = ourSwap.PairId
          let! utxos =
            utxoProvider.GetUTXOs(ourSwap.ExpectedAmount, ourCryptoCode)
          let! feeRate =
            feeEstimator.Estimate(counterPartyCryptoCode)
          let! change = getChangeAddress.Invoke(ourCryptoCode)
          let psbt =
            Transactions.createSwapPSBT
              (utxos)
              ourSwap.RedeemScript
              (ourSwap.ExpectedAmount)
              feeRate
              change
              ourSwap.TimeoutBlockHeight
              u.Network
            |> function | Ok x -> x | Error e -> failwithf "%A" e
          let! psbt = utxoProvider.SignSwapTxPSBT(psbt, ourCryptoCode)
          match psbt.TryFinalize() with
          | false, e ->
            return raise <| Exception(sprintf "%A" e)
          | true, _ ->
            let tx = psbt.ExtractTransaction()
            do! broadcaster.BroadcastTx(tx, ourCryptoCode)
            return [SwapTxPublished(tx.GetWitHash(), ourSwap.Id)]
        | _ ->
        return []
      | SwapUpdate u ->
        return raise <| Exception(sprintf "UnknownSwapId: %A"(u.Id))
      | OffChainOffer Started ->
        return []
      | OffChainOffer (Finished id) ->
        return failwith "TODO"
      | SetValidationError(id, err) when
          s.OnGoing.In |> Seq.exists(fun i -> i.Id = id)  ||
          s.OnGoing.Out |> Seq.exists(fun i -> i.Id = id) ->
        return [LoopErrored( id, err )]
      | SetValidationError(id, _err) ->
        return raise <| Exception(sprintf "UnknownSwapId: %A"id)
      | NoOp ->
        return []
    }

  let applyChanges
    { Broadcaster = broadcaster; FeeEstimator = feeEstimator; UTXOProvider = utxoProvider;
      GetChangeAddress = getChangeAddress; LightningClient = lnClient  }
    (state: State) (event: Event) =
    match event with
    | KnownSwapAddedAgain _ ->
      state, Cmd.none
    | ClaimTxPublished (txid, id) ->
      let newOuts =
        state.OnGoing.Out |> List.map(fun x -> if x.Id = id then { x with ClaimTransactionId = Some txid } else x)
      { state with OnGoing = { state.OnGoing with Out = newOuts } }, Cmd.none
    | SwapTxPublished (txid, id) ->
      let newIns =
        state.OnGoing.In |> List.map(fun x -> if x.Id = id then { x with LockupTransactionId = Some txid } else x)
      { state with OnGoing = { state.OnGoing with In = newIns } }, Cmd.none
    | NewLoopOutAdded loopOut ->
      { state with OnGoing = { state.OnGoing with Out = loopOut::state.OnGoing.Out } },
      let onSuccess =
        fun invoice ->
          let onSuccess () = (OffChainOffer(Finished(loopOut.Id)))
          Cmd.OfTask.perform
            (fun invoice -> lnClient.Offer(loopOut.PairId |> fst, invoice) :?> Task<unit>)
            (invoice)
            (onSuccess)
      let onError =
        (fun e -> SetValidationError(loopOut.Id, e) |> Cmd.ofMsg)
      PaymentRequest.Parse loopOut.Invoice
      |> ResultUtils.Result.either onSuccess onError
    | NewLoopInAdded loopIn ->
      { state with OnGoing = { state.OnGoing with In = loopIn::state.OnGoing.In } }, Cmd.none
    | LoopErrored (id, err)->
      let newLoopIns =
        state.OnGoing.In |> List.map(fun s -> if s.Id = id then { s with Error = err } else s)
      let newLoopOuts =
        state.OnGoing.Out |> List.map(fun s -> if s.Id = id then { s with Error = err } else s)
      { state with OnGoing = { state.OnGoing with In = newLoopIns; Out = newLoopOuts } }, Cmd.none

  type Aggregate = Aggregate<State, Msg, Event>
