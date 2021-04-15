namespace NLoop.Server.Services


open System
open FSharp.Control
open System.Threading.Channels
open FSharp.Control.Tasks.Affine
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NLoop.Server
open NLoop.Server.Swap

type SwapEvent =
  | Foo

type SwapEventListener(boltzClientProvider: BoltzClientProvider,
                       logger: ILogger<SwapEventListener>,
                       repositoryProvider: IRepositoryProvider,
                       opts: IOptions<NLoopOptions>) =
  inherit BackgroundService()

  override this.ExecuteAsync(stoppingToken) = unitTask {
    for c in Enum.GetValues<SupportedCryptoCode>() do
      do! this.ExecuteAsync(c, stoppingToken)
    }

  member this.ExecuteAsync(cryptoCode, stoppingToken) =
    unitTask {
        let boltzClient = boltzClientProvider.Invoke(opts.Value.GetNetwork(cryptoCode))
        let mutable notComplete = true
        while notComplete do
          let! shouldContinue = boltzClient.SwapStatusChannel.Reader.WaitToReadAsync(stoppingToken)
          notComplete <- shouldContinue
          let! swapStatus = boltzClient.SwapStatusChannel.Reader.ReadAsync(stoppingToken)
          logger.LogInformation($"Swap {swapStatus.Id} status update: {swapStatus.NewStatus.SwapStatus}")
          do! this.HandleSwapUpdate(swapStatus, cryptoCode)
    }
  member private this.HandleSwapUpdate(swapStatus, cryptoCode) = unitTask {
    let repository = repositoryProvider.GetRepository(cryptoCode)
    let! ourReverseSwap = repository.GetLoopOut(swapStatus.Id)
    let! ourSwap = repository.GetLoopIn(swapStatus.Id)
    match ourSwap, ourReverseSwap with
    | Some s, None ->
      return failwith "TODO: non-reverse swap"
    | None , Some s ->
      if (swapStatus.NewStatus.SwapStatus = s.Status) then
        logger.LogDebug($"Swap Status update is not new for us.")
        return ()
      match swapStatus.NewStatus.SwapStatus with
      | SwapStatusType.TxMempool
      | SwapStatusType.TxConfirmed ->
        let _ = swapStatus.NewStatus.Transaction
        let! feeMap = boltzClient.GetFeeEstimation()
        let n = opts.Value.GetNetwork(cryptoCode)
        let fee = failwith "todo" // FeeRate(feeMap.TryGetValue(s))
        let lockupTx = swapStatus.NewStatus.Transaction.Value.Tx // TODO: stop using Value
        let claimTx =
          Transactions.createClaimTx
            (BitcoinAddress.Create(s.ClaimAddress, n)) (s.PrivateKey) (s.Preimage) (s.RedeemScript) (fee) (lockupTx) (n)
        ()
      | _ -> ()
      return failwith ""
    | None, None ->
      return failwith "todo"
  }

  member this.RegisterLoopOut(swapId) =
    boltzClient.StartListenToSwapStatusChange(swapId)


type SwapEventListenerProvider(boltzClientProvider: BoltzClientProvider) =
  do
    failwith "todo"
