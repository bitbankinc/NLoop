namespace NLoop.Server.Services

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Payment
open FSharp.Control
open FSharp.Control.Tasks
open LndClient
open NLoop.Server


type LightningInvoiceProvider(lightningClientProvider: ILightningClientProvider) =

  interface ILightningInvoiceProvider with
    member this.GetAndListenToInvoice(cryptoCode, preimage, amt, label, routeHints, onPaymentFinished, onPaymentCancelled, ?ct) = task {
      let ct = defaultArg ct CancellationToken.None
      let client =
        lightningClientProvider
          .GetClient(cryptoCode)
      let! invoice =
        client.GetInvoice(preimage, amt, TimeSpan.FromHours(25.), routeHints, $"This is an invoice for LoopIn by NLoop (label: \"{label}\")")
      let invoiceEvent = client.SubscribeSingleInvoice(invoice.PaymentHash, ct)
      invoiceEvent
      |> AsyncSeq.iterAsync(fun s -> async {
        if s.InvoiceState = IncomingInvoiceStateUnion.Settled then
          do! onPaymentFinished(s.AmountPayed.ToMoney()) |> Async.AwaitTask
        elif s.InvoiceState = IncomingInvoiceStateUnion.Canceled then
          do! onPaymentCancelled("Offchain invoice cancelled") |> Async.AwaitTask
        })
      |> Async.StartImmediate

      return invoice
    }
