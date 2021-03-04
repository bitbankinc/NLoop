namespace NLoop.CLI
open DotNetLightning.Utils
open NLoop.Infrastructure

module private Constants =
  let MaxLoopInAcceptDelta = BlockHeightOffset16 1500us

  let MinLoopInPublishDelta = BlockHeightOffset16 10us

  let TimeoutTxConfTarget = 2

type LoopInSwap = {
  SwapKit: SwapKit
  ExecuteConfig: Dummy
}
