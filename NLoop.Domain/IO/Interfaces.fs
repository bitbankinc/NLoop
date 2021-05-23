namespace NLoop.Domain.IO

open System.Threading.Tasks
open DotNetLightning.Payment
open NBitcoin
open NLoop.Domain

type IFeeEstimator =
  abstract member Estimate: cryptoCode: SupportedCryptoCode -> Task<FeeRate>

type IBroadcaster =
  abstract member BroadcastTx: tx: Transaction * cryptoCode: SupportedCryptoCode -> Task


type IUTXOProvider =
  /// Get UTXO from your wallet
  abstract member GetUTXOs: amountToPay: Money * cryptoCode: SupportedCryptoCode -> Task<ICoin seq>
  /// Sign psbt for UTXOs provided by `GetUTXOs`
  abstract member SignSwapTxPSBT: psbt: PSBT * cryptoCode: SupportedCryptoCode -> Task<PSBT>

type INLoopLightningClient =
  abstract member Offer: cryptoCode: SupportedCryptoCode * invoice: PaymentRequest -> Task

type GetChangeAddress = delegate of SupportedCryptoCode -> Task<IDestination>
