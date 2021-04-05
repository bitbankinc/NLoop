namespace NLoop.Server

open System.Net
open System.Runtime.CompilerServices
open BTCPayServer.Lightning
open BTCPayServer.Lightning
open DotNetLightning.Payment
open DotNetLightning.Utils
open NLoop.Server
open ResultUtils

/// Layer for interoperation of BTCPayServer.Lightning and Our libraries.
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


  [<Extension>]
  static member ToNodeInfo(this: PeerConnectionString) =
    match this.EndPoint with
    | :? IPEndPoint as e ->
      NodeInfo(this.NodeId, e.Address.ToString(), e.Port)
    | :? DnsEndPoint as e ->
      NodeInfo(this.NodeId, e.Host, e.Port)
    | x -> failwith $"Unreachable {x}"
