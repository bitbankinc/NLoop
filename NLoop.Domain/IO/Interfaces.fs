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

type WalletFundingRequest = {
  CryptoCode: SupportedCryptoCode
  DestAddress: BitcoinAddress
  Amount: Money
  TargetConf: BlockHeightOffset32
}
type PayToAddress =
  WalletFundingRequest ->
    Task<Transaction>
type GetAddress = delegate of SupportedCryptoCode -> Task<Result<BitcoinAddress, string>>
