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
  | InsufficientFunds of {| WhatWeHave: Money; WhatWeNeed: Money |}

type IUTXOProvider =
  /// Get UTXO from your wallet
  abstract member GetUTXOs: amountToPay: Money * cryptoCode: SupportedCryptoCode -> Task<Result<ICoin seq, UTXOProviderError>>
  /// Sign psbt for UTXOs provided by `GetUTXOs`
  abstract member SignSwapTxPSBT: psbt: PSBT * cryptoCode: SupportedCryptoCode -> Task<PSBT>

type GetAddress = delegate of SupportedCryptoCode -> Task<Result<IDestination, string>>
