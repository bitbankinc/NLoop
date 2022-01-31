namespace NLoop.Server

open System
open DotNetLightning.Utils
open FSharp.Control.Tasks
open System.Threading
open LndClient
open NBitcoin.RPC


type RPCBlockchainClient(rpc: RPCClient) =
  interface IBlockChainClient with
    member this.GetBlock(blockHash, ?_ct) = task {
      let! resp = rpc.GetBlockAsync(blockHash, GetBlockVerbosity.WithFullTx)
      return
        {
          Height = resp.Height |> uint32 |> BlockHeight
          Block = resp.Block
        }
    }

    member this.GetBlockChainInfo(_ct) = task {
      let! resp = rpc.GetBlockchainInfoAsync()
      return {
        Progress = resp.VerificationProgress
        Height = resp.Blocks |> uint |> BlockHeight
        BestBlockHash = resp.BestBlockHash
      }
    }
    member this.GetBlockHash(height, _ct) =
      rpc.GetBlockHashAsync height.Value
    member this.GetRawTransaction(id, _ct) =
      rpc.GetRawTransactionAsync id.Value

    member this.GetBestBlockHash(_ct) =
      rpc.GetBestBlockHashAsync()

    member this.EstimateFee(target, _ct) = task {
      let! resp = rpc.EstimateSmartFeeAsync(target.Value |> int)
      return resp.FeeRate
    }
    member this.SendRawTransaction(tx, _ct) =
      rpc.SendRawTransactionAsync(tx)


type BitcoindWalletClient(rpc: RPCClient) =
  interface IWalletClient with
    member this.ListUnspent(_n, _ct) =
      task {
        let! resp = rpc.ListUnspentAsync()
        return
          resp
          |> Seq.map(WalletUtxo.FromRPCDto)
      }
    member this.SignSwapTxPSBT(psbt, _ct) = task {
      let! resp = rpc.WalletProcessPSBTAsync(psbt, sign=true)
      return resp.PSBT
    }
    member this.GetDepositAddress(_n, _ct) =
      let req = GetNewAddressRequest()
      req.AddressType <- Nullable(AddressType.P2SHSegwit)
      rpc.GetNewAddressAsync(req)
