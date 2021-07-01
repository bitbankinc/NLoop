namespace NLoop.Server
open NBitcoin
open NLoop.Domain

type ISwapEventListener =
  abstract member RegisterSwap: swapId: SwapId  * network: Network -> unit
  abstract member RemoveSwap: swapId: SwapId -> unit

