namespace NLoop.Server

open DotNetLightning.Utils
open FSharp.Control.Tasks
open System.Threading
open NBitcoin.RPC


type NLoopCoindRPCClient(rpc: RPCClient) =
  interface IBlockChainClient with
    member this.GetBlock(blockHash, _ct) = task {
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

