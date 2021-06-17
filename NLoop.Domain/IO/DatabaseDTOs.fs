namespace NLoop.Domain.IO

open System.Text.Json.Serialization
open DotNetLightning.Payment
open DotNetLightning.Utils
open NBitcoin
open Newtonsoft.Json
open NLoop.Domain
open NLoop.Domain.Utils

type LoopOut = {
  Id: SwapId
  [<JsonConverter(typeof<JsonStringEnumConverter>)>]
  Status: SwapStatusType
  Error: string
  AcceptZeroConf: bool
  [<JsonConverter(typeof<PrivKeyJsonConverter>)>]
  PrivateKey: Key
  [<JsonConverter(typeof<UInt256JsonConverter>)>]
  Preimage: uint256
  [<JsonConverter(typeof<ScriptJsonConverter>)>]
  RedeemScript: Script
  Invoice: string
  ClaimAddress: string
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  OnChainAmount: Money
  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight
  LockupTransactionId: uint256 option
  ClaimTransactionId: uint256 option
  [<JsonConverter(typeof<PairIdJsonConverter>)>]
  PairId: PairId
}

type LoopIn = {
  Id: SwapId
  [<JsonConverter(typeof<JsonStringEnumConverter>)>]
  Status: SwapStatusType
  Error: string
  [<JsonConverter(typeof<PrivKeyJsonConverter>)>]
  PrivateKey: Key
  Preimage: uint256 option
  [<JsonConverter(typeof<ScriptJsonConverter>)>]
  RedeemScript: Script
  Invoice: string
  Address: string
  [<JsonConverter(typeof<MoneyJsonConverter>)>]
  ExpectedAmount: Money
  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight
  LockupTransactionId: uint256 option
  RefundTransactionId: uint256 option
  [<JsonConverter(typeof<PairIdJsonConverter>)>]
  PairId: PairId
}
