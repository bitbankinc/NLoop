namespace NLoop.Server

open System.IO
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Payment
open DotNetLightning.Utils
open DotNetLightning.Utils.Primitives
open LndClient
open NBitcoin
open FSharp.Control.Tasks
open NLoop.Domain
open NLoop.Server.DTOs

type ISwapEventListener =
  abstract member RegisterSwap: swapId: SwapId -> unit
  abstract member RemoveSwap: swapId: SwapId -> unit

type ILightningClientProvider =
  abstract member TryGetClient: crypto: SupportedCryptoCode -> INLoopLightningClient option
  abstract member GetAllClients: unit -> INLoopLightningClient seq

type IBlockChainListener =
  abstract member CurrentHeight: BlockHeight

[<AbstractClass;Sealed;Extension>]
type ILightningClientProviderExtensions =
  [<Extension>]
  static member GetClient(this: ILightningClientProvider, crypto: SupportedCryptoCode) =
    match this.TryGetClient crypto with
    | Some v -> v
    | None -> raise <| InvalidDataException($"cryptocode {crypto} is not supported for layer 2")

  [<Extension>]
  static member AsChangeAddressGetter(this: ILightningClientProvider) =
    NLoop.Domain.IO.GetAddress(fun c ->
      task {
        match this.TryGetClient(c) with
        | None -> return Error("Unsupported Cryptocode")
        | Some s ->
          let! c = s.GetDepositAddress()
          return (Ok(c :> IDestination))
      }
    )

type OnPaymentReception = Money -> Task
type OnPaymentCancellation = string -> Task

type ILightningInvoiceProvider =
  abstract member GetAndListenToInvoice:
    cryptoCode: SupportedCryptoCode *
    preimage: PaymentPreimage *
    amt: LNMoney *
    label: string *
    routeHints: LndClient.RouteHint[] *
    onPaymentFinished: OnPaymentReception *
    onCancellation: OnPaymentCancellation *
    ct: CancellationToken option
     -> Task<PaymentRequest>

