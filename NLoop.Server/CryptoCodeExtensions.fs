namespace NLoop.Server

open DotNetLightning.Utils
open NBitcoin
open NLoop.Domain
open NLoop.Domain
open NLoop.Server

type private CryptoCodeDefaultOnChainParams = {
  SwapTxConfRequirement: BlockHeightOffset32

  /// confirmation target for estimating swap tx (htlc tx) fee rate.
  /// Used for Loop In
  HTLCConfTarget: BlockHeightOffset32

  /// SweepConfTarget for estimating sweep tx (claim tx) fee rate.
  /// Used for Loop Out
  SweepConfTarget: BlockHeightOffset32
  MaxMinerFee: Money
  /// If the sweep feerate is above this value, autolooper will not dispatch the swap.
  SweepFeeRateLimit: FeeRate
}

/// for 99.9% case the offchain crypto is BTC. So specifying default for every crypto is provably overkill.
/// But we will do it anyway for the sake of consistency.
type private CryptoCodeDefaultOffChainParams = {
  MaxPrepay: Money
  MaxRoutingFee: Money
  MaxPrepayRoutingFee: Money
}

type private CryptoCodeDefaultParams = {
  OnChain: CryptoCodeDefaultOnChainParams
  OffChain: CryptoCodeDefaultOffChainParams
}

/// The parameters specific to the PairId.
type PairIdDefaultLoopOutParameters = {
  /// Each cryptocode pair has a `rate` defined by the server. That is, price of the base asset quoted by the quote asset.
  /// However, in order to be a trustless exchange, we can't take the response from the server as granted, thus we must
  /// query other source of an price information and check the price specified by the server is in the acceptable range.
  MaxPrepay: Money
  MaxSwapFeePPM: int64<ppm>
  MaxRoutingFee: Money
  MaxPrepayRoutingFee: Money
  MaxMinerFee: Money

  /// If the sweep feerate is above this value, autolooper will not dispatch the swap.
  SweepFeeRateLimit: FeeRate

  SwapTxConfRequirement: BlockHeightOffset32
  /// SweepConfTarget for estimating sweep tx (claim tx) fee rate.
  /// Used for Loop Out
  SweepConfTarget: BlockHeightOffset32
}

type PairIdDefaultLoopInParameters = {
  /// confirmation target for estimating swap tx (htlc tx) fee rate.
  /// Used for Loop In
  HTLCConfTarget: BlockHeightOffset32
  MaxMinerFee: Money
  MaxSwapFeePPM: int64<ppm>
}

[<AutoOpen>]
module CryptoCodeExtensions =
  type SupportedCryptoCode with
    member private this.DefaultParams =
      match this with
      | SupportedCryptoCode.BTC ->
        {
          CryptoCodeDefaultParams.OnChain = {
            MaxMinerFee = 3000L |> Money.Satoshis
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
            MaxMinerFee = 3000L |> Money.Satoshis
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

  type PairId with
    member this.DefaultLoopOutParameters =
      let baseP, quoteP =
        let struct(b, q) = this.Value
        b.DefaultParams.OnChain, q.DefaultParams.OffChain
      // most default values can be derived from CryptoCode parameters.
      let p = {
          PairIdDefaultLoopOutParameters.MaxPrepay = quoteP.MaxPrepay
          MaxSwapFeePPM = 10000L<ppm> // 1%
          MaxRoutingFee = quoteP.MaxRoutingFee
          MaxPrepayRoutingFee = quoteP.MaxPrepayRoutingFee
          MaxMinerFee = baseP.MaxMinerFee
          SwapTxConfRequirement = baseP.SwapTxConfRequirement
          // in loop-out, base asset is an onchain.
          SweepConfTarget = baseP.SweepConfTarget
          SweepFeeRateLimit = baseP.SweepFeeRateLimit
        }
      match this with
      | PairId(SupportedCryptoCode.BTC, SupportedCryptoCode.BTC) ->
        {
          p
            with
            MaxSwapFeePPM = 10000L<ppm>
        }

      | PairId(SupportedCryptoCode.LTC, SupportedCryptoCode.BTC) ->
        {
          p
            with
            MaxSwapFeePPM = 30000L<ppm> // 3%
        }
      | _ -> p

    member this.DefaultLoopInParameters =
      let _baseP, quoteP =
        let struct(b, q) = this.Value
        b.DefaultParams.OffChain, q.DefaultParams.OnChain
      let p = {
        PairIdDefaultLoopInParameters.HTLCConfTarget =
          quoteP.HTLCConfTarget
        MaxMinerFee = quoteP.MaxMinerFee
        MaxSwapFeePPM = 0L<ppm> // dummy
      }

      match this.Value with
      | SupportedCryptoCode.BTC, SupportedCryptoCode.BTC ->
        {
          p
            with
            MaxSwapFeePPM  = 10000L<ppm>
        }
      | SupportedCryptoCode.BTC, SupportedCryptoCode.LTC ->
        {
          p
            with
            MaxSwapFeePPM  = 30000L<ppm>
        }
      | _ -> p
