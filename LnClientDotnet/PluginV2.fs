namespace LnClientDotnet

open System.Text.Json

type RpcParams =
  | ByPosition of JsonElement seq
  | ByName of Map<string, JsonElement>

type RpcRequest = {
  Id: JsonRPCV2Id voption
  Method: string
  Params: RpcParams
}
type RpcResult = Result<JsonElement, RPCError>
type RpcResponse = {
  Id: JsonRPCV2Id option
  Result: JsonElement
}

[<RequireQualifiedAccess>]
module Result =
  let inline toRpcResult (clightningRPCError: CLightningRPCError) (r: Result<'T, 'E>): RpcResult =
    match r with
    | Ok t -> Ok (JsonSerializer.SerializeToElement<'T> t)
    | Error e ->
      let rpcError = {
        Error = clightningRPCError
        Data = (JsonSerializer.SerializeToElement e) |> Some
      }
      Error rpcError


module private Plugin =
  let handleInit (p: RpcParams): Result<JsonElement, RPCError> =
    failwith "todo"

  let handleGetManifest (p: RpcParams): Result<JsonElement, RPCError> =
    failwith "todo"

  let handleEvent() = Ok ()

  let handleReq(sender) (req: RpcRequest): Result<JsonElement voption, RPCError> =
    match req with
    | { Id = ValueSome _; Method = method; Params = p } ->
      match method with
      | "init" -> handleInit p |> Result.map ValueSome
      | "getmanifest" ->
        handleGetManifest p |> Result.map ValueSome
      | _ ->
        Error {
          RPCError.Data = JsonSerializer.SerializeToElement(method.ToString()) |> Some
          Error = {
            Code = CLightningClientErrorCode.FromInt 3
            Msg = "unknown method"
          }
        }
    | { Id = ValueNone } ->
      match handleEvent() with
      | Ok () -> ()
      | Error () -> ()
      Ok ValueNone

  let runRpcHandler(sender) =
    ()
