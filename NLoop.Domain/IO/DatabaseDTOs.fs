namespace NLoop.Domain.IO

open System.Text.Json.Serialization
open DotNetLightning.Payment
open DotNetLightning.Utils
open NBitcoin
open NBitcoin
open Newtonsoft.Json
open NLoop.Domain

type LoopOut = {
  Id: string
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
  [<JsonConverter(typeof<PaymentRequestJsonConverter>)>]
  Invoice: PaymentRequest
  ClaimAddress: BitcoinAddress
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
  Id: string
  [<JsonConverter(typeof<JsonStringEnumConverter>)>]
  Status: SwapStatusType
  Error: string
  [<JsonConverter(typeof<PrivKeyJsonConverter>)>]
  PrivateKey: Key
  Preimage: uint256 option
  [<JsonConverter(typeof<ScriptJsonConverter>)>]
  RedeemScript: Script
  [<JsonConverter(typeof<PaymentRequestJsonConverter>)>]
  Invoice: PaymentRequest
  Address: BitcoinAddress
  ExpectedAmount: Money
  [<JsonConverter(typeof<BlockHeightJsonConverter>)>]
  TimeoutBlockHeight: BlockHeight
  LockupTransactionId: uint256 option
  RefundTransactionId: uint256 option
  [<JsonConverter(typeof<PairIdJsonConverter>)>]
  PairId: PairId
}
