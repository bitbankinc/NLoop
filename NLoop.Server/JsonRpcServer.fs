namespace NLoop.Server

open System
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open NLoop.Domain
open NLoop.Server.Actors
open NLoop.Server.DTOs
open StreamJsonRpc

type D = DefaultParameterValueAttribute
type INLoopJsonRpcServer =
  abstract member Info: unit -> Task<GetInfoResponse>
  abstract member Version: unit -> Task<string>

type NLoopJsonRpcServer(blockListener: IBlockChainListener,
                        swapExecutor: ISwapExecutor) =
  [<JsonRpcMethod("loopout")>]
  member this.LoopOut(req: LoopOutRequest, [<O;D(CancellationToken())>]ct: CancellationToken): Task<LoopOutResponse> =
    task {
      let height = blockListener.CurrentHeight req.PairIdValue.Base
      match! swapExecutor.ExecNewLoopOut(req, height, ct=ct) with
      | Ok response ->
        return response
      | Error e ->
        return raise <| Exception e
    }

  [<JsonRpcMethod("loopin")>]
  member this.LoopIn(_req: LoopInRequest) : Task<LoopInResponse> =
    task {
      return failwith "todo"
    }

  [<JsonRpcMethod("init")>]
  member this.Init() =
    task {
      return failwith "todo"
    }

  interface INLoopJsonRpcServer with
    [<JsonRpcMethod("version")>]
    member this.Version() =
      task {
        return Constants.AssemblyVersion
      }
    [<JsonRpcMethod("info")>]
    member this.Info(): Task<GetInfoResponse> =
      task {
        printfn "Info invoked"
        let response = {
          GetInfoResponse.Version = Constants.AssemblyVersion
          SupportedCoins = { OnChain = [SupportedCryptoCode.BTC; SupportedCryptoCode.LTC]
                             OffChain = [SupportedCryptoCode.BTC] }
        }
        return response
      }

