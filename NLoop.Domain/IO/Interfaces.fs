namespace NLoop.Domain.IO

open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Domain

type IFeeEstimator =
  abstract member Estimate: confTarget: BlockHeightOffset32 -> cryptoCode: SupportedCryptoCode -> Task<FeeRate>

type IBroadcaster =
  abstract member BroadcastTx: tx: Transaction * cryptoCode: SupportedCryptoCode -> Task

type UTXOProviderError =
  | InsufficientFunds of cryptoCode: SupportedCryptoCode * WhatWeHave: Money * WhatWeNeed: Money
  with
  member this.Msg =
    "utxo provider error: " +
    match this with
    | InsufficientFunds(cc, whatWeHave, whatWeNeed) ->
      $"Insufficient funds in {cc}. what we have ({whatWeHave.Satoshi} sats). " +
      $"what we need ({whatWeNeed.Satoshi} sats)"

type IUTXOProvider =
  /// Get UTXO from your wallet
  abstract member GetUTXOs: amountToPay: Money * cryptoCode: SupportedCryptoCode -> Task<Result<ICoin seq, UTXOProviderError>>
  /// Sign psbt for UTXOs provided by `GetUTXOs`
  abstract member SignSwapTxPSBT: psbt: PSBT * cryptoCode: SupportedCryptoCode -> Task<PSBT>

type GetAddress = delegate of SupportedCryptoCode -> Task<Result<IDestination, string>>
