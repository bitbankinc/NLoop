namespace NLoop.Server.Services

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Payment
open FSharp.Control
open FSharp.Control.Tasks
open LndClient
open Microsoft.Extensions.Logging
open NLoop.Server


type LightningInvoiceProvider(
  lightningClientProvider: ILightningClientProvider,
  logger: ILogger<LightningInvoiceProvider>
  ) =

  interface ILightningInvoiceProvider with
    member this.GetAndListenToInvoice(cryptoCode, preimage, amt, label, routeHints, onPaymentFinished, onPaymentCancelled, ?ct) = task {
      let ct = defaultArg ct CancellationToken.None
      let client =
        lightningClientProvider
          .GetClient(cryptoCode)
      try
        let! invoice =
          client.GetInvoice(preimage, amt, TimeSpan.FromHours(25.), routeHints, label)

        let invoiceEvent =
          let req = {
            Hash = invoice.PaymentHash
            Label = label
          }
          client.SubscribeSingleInvoice(req, ct)
        invoiceEvent
        |> AsyncSeq.iterAsync(fun s -> async {
          logger.LogDebug $"They payed to our invoice. status {s.InvoiceState}"
          if s.InvoiceState = IncomingInvoiceStateUnion.Settled then
            do! onPaymentFinished(s.AmountPayed.ToMoney()) |> Async.AwaitTask
          elif s.InvoiceState = IncomingInvoiceStateUnion.Canceled then
            do! onPaymentCancelled("Offchain invoice cancelled") |> Async.AwaitTask
          })
        |> Async.StartImmediate
        return Ok invoice
      with
      | ex ->
        return Error $"Failed to get invoice from {lightningClientProvider.Name} {ex.Message}"
    }
