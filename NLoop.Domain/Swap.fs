namespace NLoop.Domain

open System
open System
open System.Linq
open System.Text.Json
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Domain
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils
open FSharp.Control.Tasks
open FsToolkit.ErrorHandling
open NLoop.Domain.Utils.EventStore

[<RequireQualifiedAccess>]
module Swap =
  // ------ state -----
  type SwapList = {
    Out: LoopOut list
    In: LoopIn list
  }

  type State =
    | Out of LoopOut
    | In of LoopIn
    | Initialized
    with
    static member Zero = Initialized

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
      Response: SwapStatusResponseData
      Network: Network
    }

  type Msg =
    | NewLoopOut of LoopOut
    | NewLoopIn of LoopIn
    | SwapUpdate of Data.SwapStatusUpdate
    | SetValidationError of err: string

  let streamId = StreamId("swap")

  // ------ event -----
  type Event =
    | NewLoopOutAdded of LoopOut
    | NewLoopInAdded of LoopIn
    | LoopErrored of err: string
    | ClaimTxPublished of txid: uint256
    | SwapTxPublished of txid: uint256
    | ReceivedOffChainPayment of paymentPreimage: PaymentPreimage
    with
    member this.Version =
      match this with
      | NewLoopOutAdded _
      | NewLoopInAdded _
      | LoopErrored _
      | ClaimTxPublished _
      | SwapTxPublished _
      | ReceivedOffChainPayment _
       -> 0

    member this.Type =
      match this with
      | NewLoopOutAdded _ -> "new_loop_out_added"
      | NewLoopInAdded _ -> "new_loop_in_added"
      | LoopErrored _ -> "loop_errored"
      | ClaimTxPublished _ -> "claim_tx_published"
      | SwapTxPublished _ -> "swap_tx_published"
      | ReceivedOffChainPayment _ -> "received_offchain_payment"
    member this.ToEventSourcingEvent effectiveDate source : Event<Event> =
      {
        Event.Meta = { EventMeta.SourceName = source; EffectiveDate = effectiveDate }
        Type = (streamId.Value + "-" + this.Type) |> EventType.EventType
        Data = this
      }

  type Error =
    | TransactionError of string
    | UnExpectedError of exn
    | FailedToGetChangeAddress of string
    | UTXOProviderError of UTXOProviderError

  let inline private expectTxError (r: Result<_, Transactions.Error>) =
    r |> Result.mapError(fun e -> e.Message |> TransactionError)

  let private jsonConverterOpts =
    let o = JsonSerializerOptions()
    o.AddNLoopJsonConverters()
    o
  let serializer : Serializer<Event> = {
    Serializer.EventToBytes = fun e -> JsonSerializer.SerializeToUtf8Bytes(e, jsonConverterOpts)
    BytesToEvents =
      fun b ->
        try
          JsonSerializer.Deserialize(ReadOnlySpan<byte>.op_Implicit b, jsonConverterOpts)
          |> Ok
        with
        | ex ->
          $"Failed to deserialize event json\n%A{ex}"
          |> Error
  }

  // ------ deps -----
  type Deps = {
    Broadcaster: IBroadcaster
    FeeEstimator: IFeeEstimator
    UTXOProvider: IUTXOProvider
    LightningClient: INLoopLightningClient
    GetChangeAddress: GetChangeAddress
  }

  module private AsyncHelpers =
    let startPaymentRequest (lnClient: INLoopLightningClient) (loopOut: LoopOut) =
      let onSuccess =
        fun invoice ->
          let onSuccess (preimage) =
            (ReceivedOffChainPayment(preimage))
          Cmd.OfTask.perform
            (fun invoice ->
              let (struct (ourCrypto, _)) = loopOut.PairId
              lnClient.Offer(ourCrypto, invoice))
            (invoice)
            (onSuccess)
      let onError =
        (LoopErrored >> Cmd.ofMsg)
      PaymentRequest.Parse loopOut.Invoice
      |> ResultUtils.Result.either onSuccess onError

  // ----- aggregates ----

  let private enhanceEvents date source ((events, cmd) : Event list * Cmd<Event>) =
    events |> List.map(fun e -> e.ToEventSourcingEvent date source),
    cmd |> Cmd.map(fun e -> e.ToEventSourcingEvent date source)

  let executeCommand
    { Broadcaster = broadcaster; FeeEstimator = feeEstimator; UTXOProvider = utxoProvider;
      GetChangeAddress = getChangeAddress; LightningClient = lnClient  }
    (s: State)
    (cmd: ESCommand<Msg>): Task<Result<Event<Event> list * Cmd<Event<Event>>, _>> =
    taskResult {
      try
        let { CommandMeta.EffectiveDate = effectiveDate; Source = source } = cmd.Meta
        let enhance = enhanceEvents effectiveDate source
        match cmd.Data, s with
        | NewLoopOut loopOut, Initialized ->
          return ([NewLoopOutAdded loopOut], Cmd.none) |> enhance
        | NewLoopIn loopIn, Initialized ->
          return ([NewLoopInAdded loopIn], Cmd.none) |> enhance
        | SwapUpdate u, Out ourSwap ->
          if (u.Response.SwapStatus = ourSwap.Status) then
            return ([], Cmd.none)
          else
          match u.Response.SwapStatus with
          | SwapStatusType.TxMempool when not <| ourSwap.AcceptZeroConf ->
            return ([], Cmd.none)
          | SwapStatusType.TxMempool
          | SwapStatusType.TxConfirmed ->
            let (struct (ourCryptoCode, counterPartyCryptoCode)) = ourSwap.PairId
            let! feeRate =
              feeEstimator.Estimate(counterPartyCryptoCode)
            let lockupTx =
              u.Response.Transaction |> Option.defaultWith(fun () -> raise <| Exception("No Transaction in response"))
            let! claimTx =
              Transactions.createClaimTx
                (BitcoinAddress.Create(ourSwap.ClaimAddress, u.Network))
                (ourSwap.ClaimKey)
                (ourSwap.Preimage)
                (ourSwap.RedeemScript)
                (feeRate)
                (lockupTx.Tx)
                (u.Network)
              |> expectTxError
            do!
              broadcaster.BroadcastTx(claimTx, ourCryptoCode)
            let txid = claimTx.GetWitHash()
            let cmd = AsyncHelpers.startPaymentRequest lnClient ourSwap
            return ([ClaimTxPublished(txid)], cmd) |> enhance
          | _ ->
            return ([], Cmd.none)
        | SwapUpdate u, In ourSwap ->
          match u.Response.SwapStatus with
          | SwapStatusType.InvoiceSet ->
            // TODO: check confirmation?
            let (struct (ourCryptoCode, counterPartyCryptoCode)) = ourSwap.PairId
            let! utxos =
              utxoProvider.GetUTXOs(ourSwap.ExpectedAmount, ourCryptoCode)
              |> TaskResult.mapError(UTXOProviderError)
            let! feeRate =
              feeEstimator.Estimate(counterPartyCryptoCode)
            let! change =
              getChangeAddress.Invoke(ourCryptoCode)
              |> TaskResult.mapError(FailedToGetChangeAddress)
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
              return ([SwapTxPublished(tx.GetWitHash())], Cmd.none) |> enhance
          | _ ->
          return ([], Cmd.none)
        | SetValidationError(err), Out _
        | SetValidationError(err), In _ ->
          return ([LoopErrored( err )], Cmd.none) |> enhance
        | x, s ->
          return raise <| Exception($"Unexpected Command \n{x} \n\nWhile in the state\n{s}")
      with
      | ex ->
        return! UnExpectedError ex |> Error

    }

  let applyChanges
    (state: State) (event: Event) =
    match event, state with
    | ClaimTxPublished (txid), Out x ->
      Out { x with ClaimTransactionId = Some txid }
    | SwapTxPublished (txid), In x ->
      In { x with LockupTransactionId = Some txid }
    | NewLoopOutAdded loopOut, Initialized ->
      Out loopOut
    | NewLoopInAdded loopIn, Initialized ->
      In loopIn
    | LoopErrored (err), In x ->
      In { x with Error = err }
    | LoopErrored (err), Out x ->
      Out { x with Error = err }
    | ReceivedOffChainPayment(preimage), Out x ->
      Out { x with Preimage = preimage }
    | _, x -> x

  type Aggregate = Aggregate<State, Msg, Event, Error, DateTime * string>
  type Handler = Handler<State, Msg, Event, Error, SwapId>

  let getAggregate deps: Aggregate = {
    Zero = State.Zero
    Exec = executeCommand deps
    Aggregate.Apply = applyChanges
    Filter = id
    Enrich = id
    SortBy = fun event ->
      event.Meta.EffectiveDate.Value, event.Data.Type
  }

  let getRepository eventStoreUri =
    let store = eventStore eventStoreUri
    Repository.Create
      store
      serializer
      "swap"

  let getHandler aggr eventStoreUri =
    getRepository eventStoreUri
    |> Handler.Create aggr

