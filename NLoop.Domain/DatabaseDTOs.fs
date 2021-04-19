namespace NLoop.Server

open System.IO
open System.Text.Json.Serialization
open DotNetLightning.Serialization
open DotNetLightning.Utils.Primitives
open NBitcoin

type LoopOut = {
  Id: string
  [<JsonConverter(typeof<JsonStringEnumConverter>)>]
  Status: SwapStatusType
  Error: string
  AcceptZeroConf: bool
  PrivateKey: Key
  Preimage: uint256
  RedeemScript: Script
  Invoice: string
  ClaimAddress: string
  OnChainAmount: Money
  TimeoutBlockHeight: BlockHeight
  LockupTransactionId: uint256 option
  ClaimTransactionId: uint256 option
  PairId: PairId
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
