namespace NLoop.Domain.IO

open System.Threading.Tasks
open DotNetLightning.Payment
open NBitcoin
open NLoop.Domain

type IFeeEstimator =
  abstract member Estimate: cryptoCode: SupportedCryptoCode -> Task<FeeRate>

type IBroadcaster =
  abstract member BroadcastTx: tx: Transaction * cryptoCode: SupportedCryptoCode -> Task


type UTXOProviderError =
  | InsufficientFunds of leftOver: Money * whatWeWant: Money
  with
  member this.Message =
    match this with
    | InsufficientFunds(leftOver, whatWeWant) ->
      $"We can not afford to pay {whatWeWant} satoshis. We only have {leftOver.Satoshi} satoshis"

type IUTXOProvider =
  /// Get UTXO from your wallet
  abstract member GetUTXOs: amountToPay: Money * cryptoCode: SupportedCryptoCode -> Task<Result<ICoin seq, UTXOProviderError>>
  /// Sign psbt for UTXOs provided by `GetUTXOs`
  abstract member SignSwapTxPSBT: psbt: PSBT * cryptoCode: SupportedCryptoCode -> Task<PSBT>

type ILightningClient =
  abstract member Offer: invoice: PaymentRequest -> Task

type GetChangeAddress = delegate of SupportedCryptoCode -> Task<Result<IDestination, string>>
