namespace NLoop.Server.Services

open System
open System.Text.Json.Serialization
open DotNetLightning.Utils
open NBitcoin
open NLoop.Infrastructure

type GetVersionResponse = {
  Version: string
}
  with
  member private this.Triple = this.Version.Split(".")
  member this.Major = this.Triple.[0] |> Int32.Parse
  member this.Minor = this.Triple.[1] |> Int32.Parse
  member this.Patch = this.Triple.[2].Split("-").[0] |> Int32.Parse

type GetPairsResponse = {
  Info: string []
  Warnings: string []
  Pairs: Map<string, PairInfo>
}
and PairInfo = {
  Rate: double
  Limits: {| Maximal: int64; Minimal: int64; MaximalZeroConf: {|BaseAsset: int64; QuoteAsset: int64|} |}
  Fees: {|
           Percentage: double
           MinerFees: {| BaseAsset : AssetFeeInfo; QuoteAsset: AssetFeeInfo |}
         |}
  Hash: string
}
and AssetFeeInfo = {
  Normal: int64
  Reverse: {| Claim: int64; Lockup: int64 |}
}


type GetNodesResponse = {
  Nodes: Map<string, NodeInfo>
}
and NodeInfo = {
  NodeKey: PubKey
  Uris: PeerConnectionString []
}

type GetSwapTxRequest = {
  Id: string
}

type GetSwapTxResponse = {
  TransactionHex: Transaction
  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight
}
