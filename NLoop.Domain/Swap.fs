namespace NLoop.Domain

open System.Linq
open System.Threading.Tasks
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open FsToolkit.ErrorHandling

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
        | "swap.created" -> SwapStatusType.Created
        | "invoice.set" -> SwapStatusType.InvoiceSet
        | "transaction.mempool" -> SwapStatusType.TxMempool
        | "transaction.confirmed" -> SwapStatusType.TxConfirmed
        | "invoice.payed" -> SwapStatusType.InvoicePayed
        | "invoice.failedToPay" -> SwapStatusType.InvoiceFailedToPay
        | "transaction.claimed" -> SwapStatusType.TxClaimed
        | _ -> SwapStatusType.Unknown

    and SwapStatusUpdate = {
      Id: string
      Response: SwapStatusResponseData
      Network: Network
    }

  type Command =
    | NewLoopOut of LoopOut
    | NewLoopIn of LoopIn
    | SwapUpdate of Data.SwapStatusUpdate
    | SetValidationError of id: string * err: string

  // ------ event -----
  type Event =
    | KnownSwapAddedAgain of id: string
    | NewLoopOutAdded of LoopOut
    | NewLoopInAdded of LoopIn
    | LoopErrored of id: string * err: string
    | ClaimTxPublished of txid: uint256 * swapId: string

  // ------ error -----
  type Error =
    | BogusResponseFromBoltz
    | TransactionError of Transactions.Error

  // ------ deps -----
  type Deps = {
    Broadcaster: IBroadcaster
    FeeEstimator: IFeeEstimator
  }

  // ----- aggregates ----

  let executeCommand
    { Broadcaster = broadcaster; FeeEstimator = feeEstimator }
    (s: State)
    (command: Command): Task<Result<Event list, Error>> =
    taskResult {
      match command with
      | NewLoopOut loopOut when s.OnGoing.Out |> Seq.exists(fun o -> o.Id = loopOut.Id) ->
        return [KnownSwapAddedAgain loopOut.Id]
      | NewLoopOut loopOut ->
        return [NewLoopOutAdded loopOut]
      | NewLoopIn loopIn when s.OnGoing.Out |> Seq.exists(fun o -> o.Id = loopIn.Id) ->
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
          let! lockupTx =
            u.Response.Transaction |> function | Some x -> Ok x | None -> Error BogusResponseFromBoltz
          let! claimTx =
            Transactions.createClaimTx
              (BitcoinAddress.Create(ourSwap.ClaimAddress, u.Network))
              (ourSwap.PrivateKey)
              (ourSwap.Preimage)
              (ourSwap.RedeemScript)
              (feeRate)
              (lockupTx.Tx)
              (u.Network)
            |> Result.mapError(TransactionError)
          do!
            broadcaster.BroadcastTx(claimTx, ourCryptoCode)
          let txid = claimTx.GetWitHash()
          return [ClaimTxPublished(txid, ourSwap.Id)]
        | _ ->
          return []
      | SwapUpdate u when s.OnGoing.In |> Seq.exists(fun o -> u.Id = o.Id) ->
        let _ourSwap = s.OnGoing.In.First(fun o -> u.Id = o.Id)
        return []
      | SwapUpdate _u ->
        return []
      | SetValidationError(id, err) when
          s.OnGoing.In |> Seq.exists(fun i -> i.Id = id)  ||
          s.OnGoing.Out |> Seq.exists(fun i -> i.Id = id) ->
        return [LoopErrored( id, err )]
      | SetValidationError _ ->
        return []
    }

  let applyChanges (state: State) (event: Event) =
    match event with
    | KnownSwapAddedAgain _ ->
      state
    | ClaimTxPublished (txid, id) ->
      let newOuts =
        state.OnGoing.Out |> List.map(fun x -> if x.Id = id then { x with ClaimTransactionId = Some txid } else x)
      { state with OnGoing = { state.OnGoing with Out = newOuts } }
    | NewLoopOutAdded loopOut ->
      { state with OnGoing = { state.OnGoing with Out = loopOut::state.OnGoing.Out } }
    | NewLoopInAdded loopIn ->
      { state with OnGoing = { state.OnGoing with In = loopIn::state.OnGoing.In } }
    | LoopErrored (id, err)->
      let newLoopIns =
        state.OnGoing.In |> List.map(fun s -> if s.Id = id then { s with Error = err } else s)
      let newLoopOuts =
        state.OnGoing.Out |> List.map(fun s -> if s.Id = id then { s with Error = err } else s)
      { state with OnGoing = { state.OnGoing with In = newLoopIns; Out = newLoopOuts } }

  type Aggregate = Aggregate<State, Command, Event, Error, Deps>
