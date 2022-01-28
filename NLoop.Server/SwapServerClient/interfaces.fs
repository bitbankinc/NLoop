namespace NLoop.Server.SwapServerClient

open System
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils
open NBitcoin
open NLoop.Domain
open NLoop.Server
open NLoop.Server.DTOs
open FSharp.Control.Tasks
open NLoop.Server.DTOs

/// DTO against swap server (e.g. boltz-backend, loop-server)
[<RequireQualifiedAccess>]
module SwapDTO =
  type LoopOutRequest = {
    PairId: PairId
    ClaimPublicKey: PubKey
    InvoiceAmount: Money
    PreimageHash: uint256
  }
  type LoopOutResponse = {
    Id: string
    LockupAddress: string
    Invoice: PaymentRequest
    TimeoutBlockHeight: BlockHeight
    OnchainAmount: Money
    RedeemScript: Script

    /// The invoice
    MinerFeeInvoice: PaymentRequest option
  }
    with
    member this.Validate(preimageHash: uint256,
                         claimPubKey: PubKey,
                         offChainAmountWePay: Money,
                         maxSwapServiceFee: Money,
                         maxPrepay: Money,
                         rate: ExchangeRate,
                         n: Network): Result<_, string> =
      let mutable addr = null
      let mutable e = null
      try
        addr <-
          BitcoinAddress.Create(this.LockupAddress, n)
      with
      | :? FormatException as ex ->
        e <- ex
        ()
      if isNull addr then Error($"Boltz returned invalid bitcoin address for lockup address ({this.LockupAddress}): error msg: {e.Message}") else
      let actualSpk = addr.ScriptPubKey
      let expectedSpk = this.RedeemScript.WitHash.ScriptPubKey
      if (actualSpk <> expectedSpk) then
        Error $"lockupAddress {this.LockupAddress} and redeem script ({this.RedeemScript}) does not match"
      else if this.Invoice.PaymentHash <> PaymentHash(preimageHash) then
        Error "Payment Hash in invoice does not match preimage hash we specified in request"
      else
      let minerFee =
        this.MinerFeeInvoice
        |> Option.bind(fun i -> i.AmountValue)
        |> Option.map(fun a -> a.Satoshi)
        |> Option.defaultValue 0L
      match this.Invoice.AmountValue with
      | Some amt when amt.Satoshi + minerFee <> offChainAmountWePay.Satoshi  ->
        Error $"What they requested in invoice ({this.Invoice.AmountValue.Value.Satoshi} sats) does not match the amount we are expecting to pay ({offChainAmountWePay.Satoshi} sats)."
      | _ ->
        let prepayAmount =
          this.MinerFeeInvoice
          |> Option.bind(fun invoice -> invoice.AmountValue)
          |> Option.defaultValue LNMoney.Zero
        if prepayAmount.Satoshi > maxPrepay.Satoshi then
          Error $"The amount specified in invoice ({prepayAmount.Satoshi} sats) was larger than the max we can accept ({maxPrepay.Satoshi} sats)"
        else
        let swapServiceFee =
          decimal (offChainAmountWePay.Satoshi - minerFee - this.OnchainAmount.Satoshi) * rate
          |> Money.Satoshis
           // we extract 10 sats as a buffer for compensating the floating-point calculation precision
          |> fun x -> x - Money.Satoshis 10L
        if maxSwapServiceFee < swapServiceFee then
          let msg =
            $"What swap service claimed as their fee ({swapServiceFee.Satoshi} sats) is larger than our max acceptable" +
            $"fee rate ({maxSwapServiceFee.Satoshi} sats). you may want set a bigger amount to max_swap_fee in request."
          Error msg
        elif maxPrepay.Satoshi < prepayAmount.Satoshi then
          Error $"The counterparty-specified amount for prepayment miner fee ({prepayAmount}) is larger than our maximum ({maxPrepay})"
        else
          this.RedeemScript |> Scripts.validateReverseSwapScript preimageHash claimPubKey this.TimeoutBlockHeight

  type LoopInRequest = {
    PairId: PairId
    RefundPublicKey: PubKey
    Invoice: PaymentRequest
  }

  type LoopInResponse = {
    Id: string
    Address: string
    RedeemScript: Script
    AcceptZeroConf: bool
    ExpectedAmount: Money
    TimeoutBlockHeight: BlockHeight
  }
    with
    member this.Validate(preimageHash: uint256,
                         refundPubKey,
                         ourInvoiceAmount: Money,
                         maxSwapServiceFee: Money,
                         onChainNetwork: Network,
                         rate: ExchangeRate
                         ): Result<_, string> =
      let mutable addr = null
      let mutable e = null
      try
        addr <-
          BitcoinAddress.Create(this.Address, onChainNetwork)
      with
      | :? FormatException as ex ->
        e <- ex
        ()
      if isNull addr then Error $"Boltz returned invalid bitcoin address ({this.Address}): error msg: {e.Message}" else
      let actualSpk = addr.ScriptPubKey
      let expectedSpk = this.RedeemScript.WitHash.ScriptPubKey
      if (actualSpk <> expectedSpk) then
        Error $"Address {this.Address} and redeem script ({this.RedeemScript}) does not match"
      else
        let swapServiceFee =
          (decimal ourInvoiceAmount.Satoshi) - (decimal this.ExpectedAmount.Satoshi / rate)
          |> Money.Satoshis
        if maxSwapServiceFee < swapServiceFee then
          let msg =
            $"What swap service claimed as their fee ({swapServiceFee.Satoshi} sats) is larger than our max acceptable fee ({maxSwapServiceFee.Satoshi} sats)\n" +
            "You may want to specify higher max swap fee in your request."
          Error msg
        else
          (this.RedeemScript |> Scripts.validateSwapScript preimageHash refundPubKey this.TimeoutBlockHeight)

  type GetNodesResponse = {
    Nodes: Map<string, NodeInfo>
  }
  and NodeInfo = {
    NodeKey: PubKey
    Uris: PeerConnectionString []
  }

  [<RequireQualifiedAccess>]
  type UnAcceptableQuoteError =
    | SwapFeeTooExpensive of ourMax: Money * actual: Money
    | MinerFeeTooExpensive of ourMax: Money * actual: Money
    | PrepayTooExpensive of ourMax: Money * actual: Money
    | CLTVDeltaTooShort of ourMax: BlockHeightOffset32 * actual: BlockHeightOffset32
    with
    member this.Message =
      match this with
      | SwapFeeTooExpensive(ourMax, actual) ->
        $"Swap fee specified by the server is too high (ourMax: {ourMax.Satoshi} sats, The amount they specified: {actual.Satoshi} sats) "
      | MinerFeeTooExpensive(ourMax, actual) ->
        $"Miner fee specified by the server is too high (ourMax: {ourMax.Satoshi} sats, The amount they specified {actual.Satoshi} sats)"
      | PrepayTooExpensive(ourMax, actual) ->
        $"prepay fee specified by the server is too high (ourMax: {ourMax.Satoshi} sats, The amount they specified {actual.Satoshi} sats)"
      | CLTVDeltaTooShort(ourMax, actual) ->
        "CLTVDelta they say they want to specify for their invoice is too short for our HTLCConfirmation parameter. " +
        $"(our acceptable max is {ourMax}, actual value is {actual})" +
        $"You may want to set a longer value to \"{nameof(LoopOutRequest.Instance.SwapTxConfRequirement)}\" in a loopout request, " +
        "The risk of having longer value is that worst-case swap time gets longer. " +
        "This means that the counterparty will e a larger chance to have a incentive to quit the " +
        "swap in case of sudden price fluctuation. " +
        $"Another way is to make {nameof(LoopOutRequest.Instance.SwapTxConfRequirement)} shorter, you will have a larger chance" +
        $"to lose your funds in case of reorg in this case."

  type LoopOutQuoteRequest = {
    Amount: Money
    SweepConfTarget: BlockHeightOffset32
    Pair: PairId
  }

  /// estimates for the fees making up the total swap cost for the client.
  type LoopOutQuote = {
    /// An amount the service will take in swap. unit is an off-chain asset
    SwapFee: Money
    SweepMinerFee: Money
    SwapPaymentDest: PubKey
    CltvDelta: BlockHeightOffset32
    PrepayAmount: Money
  }
    with
    member this.Validate(limits: LoopOutLimits) =
      if this.SwapFee > limits.MaxSwapFee then
        (limits.MaxSwapFee, this.SwapFee) |> UnAcceptableQuoteError.SwapFeeTooExpensive |> Error
      elif this.SweepMinerFee > limits.MaxMinerFee then
        (limits.MaxMinerFee, this.SweepMinerFee) |> UnAcceptableQuoteError.MinerFeeTooExpensive |> Error
      elif this.PrepayAmount > limits.MaxPrepay then
        (limits.MaxPrepay, this.PrepayAmount) |> UnAcceptableQuoteError.PrepayTooExpensive |> Error
      else
        let ourAcceptableMaxCLTVDelta =
          limits.SwapTxConfRequirement
            + Swap.Constants.DefaultSweepConfTargetDelta
            + limits.MaxCLTVDelta
        if this.CltvDelta > ourAcceptableMaxCLTVDelta then
          (ourAcceptableMaxCLTVDelta, this.CltvDelta) |> UnAcceptableQuoteError.CLTVDeltaTooShort |> Error
        else
          Ok ()

  type LoopInQuoteRequest = {
    Amount: Money
    /// We don't need this since boltz does not require us to specify it.
    // HtlcConfTarget: BlockHeightOffset32
    Pair: PairId
  }

  type LoopInQuote = {
    /// An amount the service will take in swap. unit is an off-chain asset
    SwapFee: Money
    MinerFee: Money
  }
    with
    member this.Validate(feeLimit: LoopInLimits) =
      if this.SwapFee > feeLimit.MaxSwapFee then
        (feeLimit.MaxSwapFee, this.SwapFee) |> UnAcceptableQuoteError.SwapFeeTooExpensive |> Error
      elif this.MinerFee > feeLimit.MaxMinerFee then
        (feeLimit.MaxMinerFee, this.MinerFee) |> UnAcceptableQuoteError.MinerFeeTooExpensive |> Error
      else
        Ok ()

  [<Struct>]
  type OutTermsRequest = {
    PairId: PairId
    IsZeroConf: bool
  }

  [<Struct>]
  type InTermsRequest = {
    PairId: PairId
    IsZeroConf: bool
  }

  type OutTermsResponse = {
    MinSwapAmount: Money
    MaxSwapAmount: Money
  }

  type InTermsResponse = {
    MinSwapAmount: Money
    MaxSwapAmount: Money
  }

type ISwapServerClient =
  abstract member LoopOut: request: SwapDTO.LoopOutRequest * ?ct: CancellationToken -> Task<SwapDTO.LoopOutResponse>
  abstract member LoopIn: request: SwapDTO.LoopInRequest * ?ct: CancellationToken -> Task<SwapDTO.LoopInResponse>
  abstract member GetNodes: ?ct: CancellationToken -> Task<SwapDTO.GetNodesResponse>

  abstract member GetLoopOutQuote: request: SwapDTO.LoopOutQuoteRequest * ?ct: CancellationToken -> Task<SwapDTO.LoopOutQuote>
  abstract member GetLoopInQuote: request: SwapDTO.LoopInQuoteRequest * ?ct: CancellationToken -> Task<SwapDTO.LoopInQuote>

  abstract member GetLoopOutTerms: req: SwapDTO.OutTermsRequest * ?ct : CancellationToken -> Task<SwapDTO.OutTermsResponse>
  abstract member GetLoopInTerms: req: SwapDTO.InTermsRequest * ?ct : CancellationToken -> Task<SwapDTO.InTermsResponse>

  abstract member CheckConnection: ?ct: CancellationToken -> Task

  /// In loop out, the counterparty must tell us about the swap tx (htlc tx, lockup tx). Otherwise we have no way to
  /// know about it. This is a method for waiting about the tx information.
  abstract member ListenToSwapTx: swapId: SwapId * onSwapTx: (Transaction -> Task)  * ?ct: CancellationToken -> Task


type RestrictionError =
  | MinimumExceedsMaximumAmt
  | MaxExceedsServer of clientMax: Money * serverMax: Money
  | MinLessThenServer of clientMin: Money * serverMin: Money
  with
  member this.Message =
    match this with
    | MinimumExceedsMaximumAmt -> "minimum swap amount exceeds maximum"
    | MaxExceedsServer (c, s) ->
      $"maximum swap amount ({c.Satoshi} sats) is more than the server maximum ({s.Satoshi} sats)"
    | MinLessThenServer (c, s)  ->
      $"minimum swap amount ({c.Satoshi} sats) is less than server minimum ({s.Satoshi} sats)"

type ClientRestrictions = {
  OutMinimum: Money option
  OutMaximum: Money option
  InMinimum: Money option
  InMaximum: Money option
}
  with
  static member NoRestriction = {
    OutMinimum = None
    OutMaximum = None
    InMinimum = None
    InMaximum = None
  }

  static member FromMaybeUnaryMinMax min max = {
    OutMaximum = max
    OutMinimum = min
    InMaximum = max
    InMinimum = min
  }
  static member FromUnaryMinMax min max =
    ClientRestrictions.FromMaybeUnaryMinMax(Some min) (Some max)

open FsToolkit.ErrorHandling

type ServerRestrictions = {
  Minimum: Money
  Maximum: Money
}
  with
  member this.Validate
    (cR: ClientRestrictions, cat: Swap.Category) =
    let check maybeClientMin maybeClientMax =
      match maybeClientMin, maybeClientMax with
      | Some clientMin, Some clientMax when clientMin > clientMax ->
        Error(RestrictionError.MinimumExceedsMaximumAmt)
      | Some clientMin, _  when clientMin < this.Minimum ->
        Error(RestrictionError.MinLessThenServer(clientMin, this.Minimum))
      | _, Some clientMax when clientMax > this.Maximum ->
          Error(RestrictionError.MaxExceedsServer(clientMax, this.Maximum))
      | _ ->
        Ok()
    match cat with
    | Swap.Category.Out ->
      check cR.OutMinimum cR.OutMaximum
    | Swap.Category.In ->
      check cR.InMinimum cR.InMaximum

  member this.Validate(cr: ClientRestrictions) =
    result {
      do! this.Validate(cr, Swap.Category.Out)
      do! this.Validate(cr, Swap.Category.In)
    }


[<Extension;AbstractClass;Sealed>]
type ISwapServerClientExtensions =
  [<Extension>]
  static member GetSwapAmountRestrictions(this: ISwapServerClient, group: Swap.Group, zeroConf: bool, ?ct: CancellationToken) = task {
    let ct = defaultArg ct CancellationToken.None
    match group.Category with
    | Swap.Category.In ->
      let! resp = this.GetLoopInTerms({
        SwapDTO.InTermsRequest.PairId = group.PairId
        SwapDTO.InTermsRequest.IsZeroConf = zeroConf
      }, ct )
      return {
        ServerRestrictions.Maximum = resp.MaxSwapAmount
        Minimum = resp.MinSwapAmount
      }
    | Swap.Category.Out ->
      let! resp = this.GetLoopOutTerms({
          SwapDTO.OutTermsRequest.PairId = group.PairId
          SwapDTO.OutTermsRequest.IsZeroConf = zeroConf
        }, ct)
      return {
        ServerRestrictions.Maximum = resp.MaxSwapAmount
        Minimum = resp.MinSwapAmount
      }
    }
