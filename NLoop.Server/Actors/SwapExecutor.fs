namespace NLoop.Server.Actors

open System
open System.Collections.Generic
open FSharp.Control
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open FSharp.Control.Reactive
open FsToolkit.ErrorHandling
open LndClient
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.Options
open NLoop.Server.DTOs
open NLoop.Server.SwapServerClient
type SwapExecutor(
                  invoiceProvider: ILightningInvoiceProvider,
                  opts: GetOptions,
                  logger: ILogger<SwapExecutor>,
                  eventAggregator: IEventAggregator,
                  swapServerClient: ISwapServerClient,
                  tryGetExchangeRate: TryGetExchangeRate,
                  lightningClientProvider: ILightningClientProvider,
                  getSwapKey: GetSwapKey,
                  getSwapPreimage: GetSwapPreimage,
                  getNetwork: GetNetwork,
                  getAddress: GetAddress,
                  getWallet: GetWalletClient,
                  swapActor: ISwapActor,
                  listeners: IEnumerable<ISwapEventListener>
  )=


  interface ISwapExecutor with
    /// Helper function for creating new loop out.
    /// Technically, the logic of this function should be in the Domain layer, but we want
    /// swapId to be the StreamId of the event stream, thus we have to
    /// get the `StreamId` outside of the Domain, So we must call `BoltzClient.CreateReverseSwap` and get the swapId before
    /// sending the command into the domain layer.
    /// If we define some internal UUID for swapid instead of using the one given by the boltz server, the logic of this
    /// function can go into the domain layer. But that complicates things by having two kinds of IDs for each swaps.
    member this.ExecNewLoopOut(
                               req: LoopOutRequest,
                               height: BlockHeight,
                               ?s,
                               ?ct
                               ) = taskResult {
        let s = defaultArg s (nameof(SwapExecutor))
        let ct = defaultArg ct CancellationToken.None
        let! claimKey = getSwapKey()
        let! preimage = getSwapPreimage()
        let preimageHash = preimage.Hash
        let pairId =
          req.PairIdValue

        let n = getNetwork(pairId.Base)
        let! outResponse =
          let req =
            { SwapDTO.LoopOutRequest.InvoiceAmount = req.Amount
              SwapDTO.LoopOutRequest.PairId = pairId
              SwapDTO.LoopOutRequest.ClaimPublicKey = claimKey.PubKey
              SwapDTO.LoopOutRequest.PreimageHash = preimageHash.Value }
          swapServerClient.LoopOut req

        ct.ThrowIfCancellationRequested()
        let! addr =
          match req.Address with
          | Some a -> TaskResult.retn a
          | None ->
            getAddress.Invoke(pairId.Base) |> TaskResult.map(fun a -> a.ToString())
        ct.ThrowIfCancellationRequested()
        let loopOut = {
          LoopOut.Id = outResponse.Id |> SwapId
          ClaimKey = claimKey
          OutgoingChanIds = req.OutgoingChannelIds
          Preimage = preimage
          RedeemScript = outResponse.RedeemScript
          Invoice = outResponse.Invoice.ToString()
          ClaimAddress = addr
          OnChainAmount = outResponse.OnchainAmount
          TimeoutBlockHeight = outResponse.TimeoutBlockHeight
          SwapTxHex = None
          ClaimTransactionId = None
          PairId = pairId
          ChainName = opts().ChainName.ToString()
          Label = req.Label |> Option.defaultValue String.Empty
          PrepayInvoice =
            outResponse.MinerFeeInvoice
            |> Option.map(fun s -> s.ToString())
            |> Option.defaultValue String.Empty
          MaxMinerFee = req.Limits.MaxMinerFee
          SweepConfTarget =
            req.SweepConfTarget
            |> ValueOption.map(uint >> BlockHeightOffset32)
            |> ValueOption.defaultValue pairId.DefaultLoopOutParameters.SweepConfTarget
          IsClaimTxConfirmed = false
          IsOffchainOfferResolved = false
          Cost = SwapCost.Zero
          LoopOut.SwapTxConfRequirement =
            req.Limits.SwapTxConfRequirement
          SwapTxHeight = None
        }
        let! exchangeRate =
          tryGetExchangeRate(pairId, ct)
          |> Task.map(Result.requireSome $"exchange rate for {pairId} is not available")
        match outResponse.Validate(preimageHash.Value,
                                   claimKey.PubKey,
                                   req.Amount,
                                   req.Limits.MaxSwapFee,
                                   req.Limits.MaxPrepay,
                                   exchangeRate,
                                   n) with
        | Error e ->
          return! Error e
        | Ok () ->
          let group = {
            Swap.Group.Category = Swap.Category.Out
            Swap.Group.PairId = pairId
          }
          for l in listeners do
            l.RegisterSwap(loopOut.Id, group)
          let loopOutParams = {
            Swap.LoopOutParams.MaxPrepayFee = req.MaxPrepayRoutingFee |> ValueOption.defaultValue(Money.Coins 100000m)
            Swap.LoopOutParams.MaxPaymentFee = req.MaxSwapFee |> ValueOption.defaultValue(Money.Coins 100000m)
            Swap.LoopOutParams.Height = height
          }
          let obs =
            getObs eventAggregator loopOut.Id
            |> Observable.replay
          use _ = obs.Connect()
          do! swapActor.Execute(loopOut.Id, Swap.Command.NewLoopOut(loopOutParams, loopOut), s)
          let! maybeClaimTxId =
            let chooser =
              if loopOut.AcceptZeroConf then
                (function | Swap.Event.ClaimTxPublished { Txid = txId }  -> Some (Some txId) | _ -> None)
              else
                (function | Swap.Event.NewLoopOutAdded _  -> Some None | _ -> None)
            obs
            |> Observable.chooseOrError chooser
          return
            {
              LoopOutResponse.Id = loopOut.Id.Value
              Address = loopOut.ClaimAddress
              ClaimTxId = maybeClaimTxId
            }
    }
    member this.ExecNewLoopIn(loopIn: LoopInRequest, height: BlockHeight, ?source, ?ct) = taskResult {
        let ct = defaultArg ct CancellationToken.None
        let source = defaultArg source (nameof(SwapExecutor))
        let pairId =
          loopIn.PairIdValue

        let group = {
          Swap.Group.Category = Swap.Category.In
          Swap.Group.PairId = pairId
        }
        let onChainNetwork = getNetwork(group.OnChainAsset)
        let! refundKey = getSwapKey()
        let! preimage = getSwapPreimage()

        let mutable maybeSwapId = None

        let lnClient = lightningClientProvider.GetClient(group.OffChainAsset)
        // -- fee rate check --
        let htlcConfTarget =
          loopIn.HtlcConfTarget
          |> ValueOption.map(uint >> BlockHeightOffset32)
          |> ValueOption.defaultValue pairId.DefaultLoopInParameters.HTLCConfTarget
        let wallet = getWallet group.OnChainAsset
        let! swapTxFee =
          // we use p2sh-p2wsh to estimate the worst case fee.
          let addr = Scripts.dummySwapScriptV1.WitHash.ScriptPubKey.Hash.GetAddress(onChainNetwork)
          let d = Dictionary<BitcoinAddress, _>()
          d.Add(addr, loopIn.Amount)
          wallet.GetSendingTxFee(d, htlcConfTarget)
          |> TaskResult.mapError(fun walletError -> walletError.ToString())
        if loopIn.Limits.MaxMinerFee < swapTxFee then
          let msg =
            $"OnChain FeeRate is too high. (actual fee: {swapTxFee.Satoshi} sats. Our maximum: " +
            $"{loopIn.Limits.MaxMinerFee.Satoshi} sats)"
          return! Error msg
        else
        // -- --

        let! channels = lnClient.ListChannels()
        let! maybeRouteHints =
          match loopIn.ChannelId with
          | Some cId ->
            lnClient.GetRouteHints(cId, ct)
            |> Task.map(Array.singleton)
          | None ->
            match loopIn.LastHop with
            | None -> Task.FromResult([||])
            | Some pk ->
              channels
              |> Seq.filter(fun c -> c.NodeId = pk)
              |> Seq.map(fun c ->
                // todo: do not add route hints for all possible channel.
                // Instead we should decide which channel is the one we want payment through.
                lnClient.GetRouteHints(c.Id, ct)
              )
              |> Task.WhenAll
        let! invoice =
          let amt = loopIn.Amount.ToLNMoney()
          let onPaymentFinished = fun (amt: Money) ->
            logger.LogInformation $"Received on-chain payment for loopIn swap {maybeSwapId}"
            match maybeSwapId with
            | Some i ->
              swapActor.Execute(i, Swap.Command.CommitReceivedOffChainPayment(amt), (nameof(invoiceProvider)))
            | None ->
              // This will never happen unless they pay us unconditionally.
              Task.CompletedTask

          let onPaymentCanceled = fun (msg: string) ->
            logger.LogWarning $"Invoice for the loopin swap {maybeSwapId} has been cancelled"
            match maybeSwapId with
            | Some i ->
              swapActor.Execute(i, Swap.Command.MarkAsErrored(msg), nameof(invoiceProvider))
            | None ->
              // This will never happen unless they pay us unconditionally.
              Task.CompletedTask
          let label =
            loopIn.Label
            |> Option.defaultValue String.Empty
            |> fun s -> s + $"(id: {(Guid.NewGuid().ToString())})"
          invoiceProvider.GetAndListenToInvoice(
            group.OffChainAsset,
            preimage,
            amt,
            label,
            maybeRouteHints,
            onPaymentFinished, onPaymentCanceled, None)

        ct.ThrowIfCancellationRequested()
        try
          let! inResponse =
            let req =
              { SwapDTO.LoopInRequest.Invoice = invoice
                SwapDTO.LoopInRequest.PairId = pairId
                SwapDTO.LoopInRequest.RefundPublicKey = refundKey.PubKey }
            swapServerClient.LoopIn req
          let swapId = inResponse.Id |> SwapId
          ct.ThrowIfCancellationRequested()
          maybeSwapId <- swapId |> Some
          let! exchangeRate =
            tryGetExchangeRate(pairId, ct)
            |> Task.map(Result.requireSome $"exchange rate for {PairId.toString(&pairId)} is not available")
          match inResponse.Validate(invoice.PaymentHash.Value,
                                    refundKey.PubKey,
                                    loopIn.Amount,
                                    loopIn.Limits.MaxSwapFee,
                                    onChainNetwork,
                                    exchangeRate) with
          | Error e ->
            return! Error(e)
          | Ok addressType ->
            for l in listeners do
              l.RegisterSwap(swapId, group)
            let loopIn = {
              LoopIn.Id = swapId
              RefundPrivateKey = refundKey
              Preimage = None
              RedeemScript = inResponse.RedeemScript
              Invoice = invoice.ToString()
              ExpectedAmount = inResponse.ExpectedAmount
              TimeoutBlockHeight = inResponse.TimeoutBlockHeight
              SwapTxInfoHex = None
              RefundTransactionId = None
              PairId = pairId
              ChainName = opts().ChainName.ToString()
              Label = loopIn.Label |> Option.defaultValue String.Empty
              HTLCConfTarget =
                htlcConfTarget
              Cost = SwapCost.Zero
              AddressType = addressType
              MaxMinerFee =
                loopIn.Limits.MaxMinerFee
              MaxSwapFee =
                loopIn.Limits.MaxSwapFee
              IsOffChainPaymentReceived = false
              IsTheirSuccessTxConfirmed = false
              LastHop = maybeRouteHints.TryGetLastHop()
            }
            let obs =
              getObs(eventAggregator) swapId
              |> Observable.replay
            use _ = obs.Connect()
            do! swapActor.Execute(swapId, Swap.Command.NewLoopIn(height, loopIn), source)
            let! () =
              obs
              |> Observable.chooseOrError
                (function | Swap.Event.NewLoopInAdded _ -> Some () | _ -> None)
            return {
              LoopInResponse.Id = loopIn.Id.Value
              Address = loopIn.SwapAddress.ToString()
            }
        with
        | :? HttpRequestException as ex ->
          let msg = ex.Message.Replace("\"", "")
          return! Error($"Error requesting to boltz ({msg})")
      }
