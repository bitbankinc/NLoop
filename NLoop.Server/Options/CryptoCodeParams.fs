namespace NLoop.Server.Options

open DotNetLightning.Utils
open NBitcoin
open NLoop.Domain

type CryptoCodeDefaultOnChainParams = {
  SwapTxConfRequirement: BlockHeightOffset32

  /// confirmation target for estimating swap tx (htlc tx) fee rate.
  /// Used for Loop In
  HTLCConfTarget: BlockHeightOffset32

  /// SweepConfTarget for estimating sweep tx (claim tx) fee rate.
  /// Used for Loop Out
  SweepConfTarget: BlockHeightOffset32
  /// default limit we place on miner fees per swap.
  /// We apply a multiplier to this default fee to guard against the case where we have broadcast the preimage,
  /// and then fees spike and we need to sweep the preimage.
  MaxMinerFee: Money
  /// If the sweep feerate is above this value, autolooper will not dispatch the swap.
  SweepFeeRateLimit: FeeRate
}

/// for 99.9% case the offchain crypto is BTC. So specifying default for every crypto is provably overkill.
/// But we will do it anyway for the sake of consistency.
type  CryptoCodeDefaultOffChainParams = {
  MaxPrepay: Money
  MaxRoutingFee: Money
  MaxPrepayRoutingFee: Money
}

type CryptoCodeDefaultParams = {
  OnChain: CryptoCodeDefaultOnChainParams
  OffChain: CryptoCodeDefaultOffChainParams
}

[<AutoOpen>]
module CryptoCodeExtensions =
  type SupportedCryptoCode with
    member this.DefaultParams =
      match this with
      | SupportedCryptoCode.BTC ->
        {
          CryptoCodeDefaultParams.OnChain = {
            MaxMinerFee = 15000L * 100L |> Money.Satoshis
            SwapTxConfRequirement = 1u |> BlockHeightOffset32
            HTLCConfTarget = 10u |> BlockHeightOffset32
            SweepConfTarget = 100u |> BlockHeightOffset32
            SweepFeeRateLimit = FeeRate(feePerK=Money.Satoshis(1000L))
          }
          OffChain = {
            MaxPrepay = 1000L |> Money.Satoshis
            MaxRoutingFee = 100L |> Money.Satoshis
            MaxPrepayRoutingFee = 100L |> Money.Satoshis
          }
        }
      | SupportedCryptoCode.LTC ->
        {
          CryptoCodeDefaultParams.OnChain = {
            MaxMinerFee = 3000L * 100L |> Money.Satoshis
            SwapTxConfRequirement = 3u |> BlockHeightOffset32
            HTLCConfTarget = 10u |> BlockHeightOffset32
            SweepConfTarget = 100u |> BlockHeightOffset32
            SweepFeeRateLimit = FeeRate(feePerK=Money.Satoshis(200L))
          }
          OffChain = {
            MaxPrepay = 1000L |> Money.Satoshis
            MaxRoutingFee = 100L |> Money.Satoshis
            MaxPrepayRoutingFee = 100L |> Money.Satoshis
          }
        }
      | x -> failwith $"Unknown CryptoCode {x}"

    member this.GetDefaultOptions() =
      match this with
      | SupportedCryptoCode.BTC ->
        BTCChainOptions() :> IChainOptions
      | SupportedCryptoCode.LTC ->
        LTCChainOptions() :> IChainOptions
      | x -> failwith $"Unknown CryptoCode {x}"
