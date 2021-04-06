namespace NLoop.Server

open System.IO
open DotNetLightning.Serialization
open DotNetLightning.Utils.Primitives
open NBitcoin
open NBitcoin.DataEncoders
open NBitcoin.DataEncoders
open NLoop.Server.DTOs

type SwapUpdateEvent = int

type LoopOut = {
  Id: string
  State: SwapStatusType
  Error: string
  Status: SwapUpdateEvent
  AcceptZeroConf: bool
  PrivateKey: Key
  Preimage: uint256
  RedeemScript: Script
  Invoice: string
  ClaimAddress: string
  OnChainAmount: int64
  TimeoutBlockHeight: BlockHeight
  LockupTransactionId: uint256
  ClaimTransactionId: uint256
  CryptoCode: SupportedCryptoCode
}

/// TODO
type LoopIn = {
  Id: string
}
