namespace NLoop.Infrastructure.DTOs

open DotNetLightning.Utils.Primitives
open NBitcoin

[<CLIMutable>]
type LoopInRequest = {
  Amount: Money
  External: bool
  ConfTarget: int
  LastHop: NodeId
  Label: string option
}

[<CLIMutable>]
type LoopOutRequest = {
  Amount: Money
  External: bool
  ConfTarget: int
  LastHop: NodeId
  Label: string option
}

[<CLIMutable>]
type LoopInResponse = {
  Id: string
}

[<CLIMutable>]
type LoopOutResponse = {
  Id: string
}
