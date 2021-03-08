namespace NLoop.Infrastructure.DTOs

open DotNetLightning.Utils.Primitives
open NBitcoin

type LoopInRequest = {
  Amount: Money
  External: bool
  ConfTarget: int
  LastHop: NodeId
  Label: string option
}

type LoopOutRequest = {
  /// The
  Channel: ShortChannelId seq
  Address: BitcoinAddress option
  CounterPartyPair: INetworkSet option
  Amount: Money
  External: bool
  ConfTarget: int option
  LastHop: NodeId
  Label: string option
}

type LoopInResponse = {
  Id: string
}

type LoopOutResponse = {
  Id: string
}
