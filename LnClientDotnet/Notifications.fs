namespace LnClientDotnet

open System
open System.Net
open System.Text.Json
open System.Text.Json.Serialization
open DotNetLightning.Utils
open DotNetLightning.Utils.OnionError
open NBitcoin

type ClightningSatoshiJsonEncoder () =
  inherit JsonConverter<LNMoney>()

  override this.Write(writer, value, options) =
    failwith "todo"
  override this.Read(reader, value, options) =
    failwith "todo"

type ChannelOpened = {
  Id: string
  FundingSatoshis: LNMoney
  FundingTxId: uint256
  FundingLocked: bool
}

type ChannelOpenFailed = {
  ChannelId: OutPoint
}

type ChannelState =
  | CHANNELD_AWAITING_LOCKIN = 2
  | CHANNELD_NORMAL = 3
  | CHANNELD_SHUTTING_DOWN = 4

  | CLOSINGD_SIGEXCHANGE = 5
  | CLOSINGD_COMPLETE = 6
  | AWAITING_UNILATERAL = 7
  | ONCHAIN = 8
  | CLOSED = 9
  | DUALOPEND_OPEN_INIT = 10
  | DUALOPEND_AWITING_LOCKIN = 11

[<RequireQualifiedAccess>]
type Cause =
  | Unknown
  | Local
  | User
  | Remote
  | Protocol
  | Onchain

type ChannelStateChanged = {
  PeerId: PubKey
  ChannelId: OutPoint
  ShortChannelId: ShortChannelId
  OldState: ChannelState
  NewState: ChannelState
  Cause: Cause
  Message: string
}

[<RequireQualifiedAccess>]
type ConnectionDirection =
  | In | Out

/// sent when new connection to a peer is established
type Connect = {
  Id: PubKey
  Direction: ConnectionDirection
  Address: EndPoint
}
type Disconnect = {
  Id: PubKey
}

type InvoicePayment = {
  Label: string
  Preimage: PaymentPreimage
  Msat: LNMoney
}

[<RequireQualifiedAccess>]
type WarningLevel =
  | Warn | Error

type Warning = {
  Level: WarningLevel
  Time: string
  Source: string
  Log: string
}

[<RequireQualifiedAccess>]
type PaymentForwardStatus =
  | Offered | Settled | Failed | LocalFailed

type ForwardEvent = {
  PaymentHash: PaymentHash
  InChannel: ShortChannelId
  OutChannel: ShortChannelId
  InMsatoshi: LNMoney
  OutMsatoshi: LNMoney
  Fee:  LNMoney
  FeeMsat: LNMoney
  Status: PaymentForwardStatus
  ReceivedTime: DateTime
  ResolvedTime: DateTime option
  Failcode: FailureCode option
  Failreason : string option
}

/// sent every time sendpay succeeds (with `complete` status)
type SendpaySuccess = {
  Id: int
}
