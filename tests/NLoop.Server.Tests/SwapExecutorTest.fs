namespace NLoop.Server.Tests

open System
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open NBitcoin
open NLoop.Domain
open NLoop.Domain.Utils
open NLoop.Server
open NLoop.Server.Actors
open Xunit
open FSharp.Control.Tasks
open FSharp.Control.Reactive

type SwapExecutorTest() =

  member private this.TestConcurrentUpdateCore(useInMemoryDB: bool) =
    task {
      use provider =
        TestHelpers.GetTestServiceProvider(fun (sp: IServiceCollection) ->
          sp.AddLogging()
          |> ignore
          sp.AddSingleton<NLoop.Domain.Utils.Store>(
            if useInMemoryDB then InMemoryStore.getEventStore() else (EventStore.eventStore(TestHelpersMod.eventStoreUrl |> Uri))
          )
          |> ignore
          ()
        )
      let swapExecutor =
        provider.GetRequiredService<ISwapActor>()

      let swapId = SwapId(if useInMemoryDB then "test-swap-id" else (Guid.NewGuid().ToString()))
      let b =
        { Height = BlockHeight.Zero
          Block =
            Network.RegTest.GetGenesis()
        }
      let b1 = b.CreateNext(pubkey1.WitHash.GetAddress(Network.RegTest))
      let obs =
        getObs (provider.GetRequiredService<IEventAggregator>()) swapId
        |> Observable.replay
      use _ = obs.Connect()

      use cts = new CancellationTokenSource()
      cts.CancelAfter(5000)
      let loopOutCmd =
        let loopOutParams = {
          Swap.LoopOutParams.MaxPrepayFee = Money.Coins 100000m
          Swap.LoopOutParams.MaxPaymentFee = Money.Coins 100000m
          Swap.LoopOutParams.Height = BlockHeight.Zero
        }
        Swap.Command.NewLoopOut(loopOutParams, loopOut1)
      do! swapExecutor.Execute(swapId, loopOutCmd, nameof(this.TestConcurrentUpdateCore))
      // execute 100 commands in parallel
      do!
        Array.create 100 (Swap.Command.NewBlock(b1, SupportedCryptoCode.BTC))
        |> Array.map(fun cmd -> swapExecutor.Execute(swapId, cmd))
        |> Task.WhenAll
      let! result =
        obs
        |> Observable.chooseOrError
          (function | Swap.Event.NewTipReceived _ -> Some () | _ -> None)
        |> fun a -> Async.StartAsTask(a, TaskCreationOptions.None, cts.Token)
      // ... and there is no error
      Assertion.isOk result
      ()
    }

  [<Fact>]
  member this.TestConcurrentUpdateInMemory() =
    this.TestConcurrentUpdateCore true

  [<Fact>]
  [<Trait("Docker", "On")>]
  member this.TestConcurrentUpdateEventStore() =
    this.TestConcurrentUpdateCore false
