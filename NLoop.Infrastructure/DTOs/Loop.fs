namespace NLoop.Infrastructure.DTOs

module Loop =
  open System
  open DotNetLightning.Utils
  open NBitcoin
  open NLoop.Infrastructure.DTOs.SwapState

  type SwapContract = private {
    Preimage: PaymentPreimage
    AmountRequested: LNMoney
    SenderKey: PubKey
    ReceiverKey: PubKey
    CLTVExpiry: BlockHeight
    MaxSwapFee: LNMoney
    MaxMinerFee: Money
    InitiationHeight: BlockHeight
    InitiationTime: DateTimeOffset
    Label: string
    ProtocolVersion: uint8
  }

  type LoopEvent = {
    SwapStateData: SwapStateData
    Time: DateTimeOffset
  }

  type Loop = {
    Hash: uint256
    Events: LoopEvent[]
  }
