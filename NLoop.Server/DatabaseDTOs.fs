namespace NLoop.Server

open System.IO
open System.Text.Json.Serialization
open DotNetLightning.Serialization
open DotNetLightning.Utils.Primitives
open NBitcoin
open NBitcoin.DataEncoders
open NBitcoin.DataEncoders
open NLoop.Server.DTOs

type SwapUpdateEvent = int

type LoopOut = {
  Id: string
  [<JsonConverter(typeof<JsonStringEnumConverter>)>]
  State: SwapStatusType
  Error: string
  Status: SwapUpdateEvent
  AcceptZeroConf: bool
  [<JsonConverter(typeof<PrivKeyJsonConverter>)>]
  PrivateKey: Key
  [<JsonConverter(typeof<UInt256JsonConverter>)>]
  Preimage: uint256
  [<JsonConverter(typeof<ScriptJsonConverter>)>]
  RedeemScript: Script
  Invoice: string
  ClaimAddress: string
  OnChainAmount: int64
  TimeoutBlockHeight: BlockHeight
  [<JsonConverter(typeof<UInt256JsonConverter>)>]
  LockupTransactionId: uint256
  [<JsonConverter(typeof<UInt256JsonConverter>)>]
  ClaimTransactionId: uint256
  CryptoCode: SupportedCryptoCode
}

/// TODO
type LoopIn = {
  Id: string
}
/// TODO
type LoopOutSimple = {
  Id: string
  State: SwapStatusType
  Error: string
  //Status: SwapUpdateEvent
  AcceptZeroConf: bool
  //PrivateKey: Key
  Preimage: uint256
  //RedeemScript: Script
  Invoice: string
  ClaimAddress: string
  OnChainAmount: int64
  TimeoutBlockHeight: BlockHeight
  LockupTransactionId: uint256
  ClaimTransactionId: uint256
  CryptoCode: SupportedCryptoCode
}
