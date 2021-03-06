namespace NLoop.Server

open System.Runtime.CompilerServices
open BTCPayServer.Lightning
open DotNetLightning.Payment
open DotNetLightning.Utils
open ResultUtils

[<AbstractClass;Sealed;Extension>]
type BTCPayServerLightningExtensions() =
  [<Extension>]
  static member ToDNLInvoice(this: LightningInvoice) =
    this.BOLT11
    |> PaymentRequest.Parse
    |> Result.deref

  [<Extension>]
  static member ToLightMoney(this: LNMoney) =
    this.MilliSatoshi |> LightMoney.MilliSatoshis
