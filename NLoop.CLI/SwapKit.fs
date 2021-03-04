namespace NLoop.CLI

open System
open DotNetLightning.Utils.Primitives
open NBitcoin
open NLoop.Infrastructure.DTOs
open NLoop.Infrastructure.DTOs.Loop
open NLoop.Infrastructure.DTOs.SwapState

type SwapKit = {
  Hash: uint256
  Height: BlockHeight
  LastUpdateTime: DateTimeOffset
  Cost: SwapCost
  State: SwapState
  Contract: SwapContract
  SwapType: SwapType
}
