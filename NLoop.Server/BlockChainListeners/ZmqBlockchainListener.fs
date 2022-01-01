namespace NLoop.Server

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open FSharp.Control
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NBitcoin
open NBitcoin.DataEncoders
open NLoop.Domain
open FSharp.Control.Tasks

open NLoop.Server
open NLoop.Server.Projections
open NetMQ
open NetMQ.Sockets

type FilterType =
  | RawTx
  | HashTx
  | RawBlock
  | HashBlock
  | Sequence
  with
  member this.Topic =
    match this with
    | RawTx -> "rawtx"
    | HashTx -> "hashtx"
    | RawBlock -> "rawblock"
    | HashBlock -> "hashblock"
    | Sequence -> "sequence"

[<AutoOpen>]
module private ZmqHelpers =
  let utf8ToBytes (str: string) = System.Text.Encoding.UTF8.GetBytes(str)
  let bytesToUtf8 (b: byte[]) = System.Text.Encoding.UTF8.GetString b
  let hex = HexEncoder()
  let hashblockB = "hashblock" |>  utf8ToBytes
  let hashtxB = "hashtx" |>  utf8ToBytes
  let rawblockB = "rawblock" |>  utf8ToBytes
  let rawtxB = "rawtx" |> utf8ToBytes
  let sequenceB = "sequence" |> utf8ToBytes

type private OnBlock = Block -> unit
type ZmqClient(logger: ILogger<ZmqClient>, address) =
  let sock = new SubscriberSocket()
  let runtime = new NetMQRuntime()
  let mutable _executingTask = null

  do
    for bTypes in seq [RawBlock; HashBlock] do
      sock.Subscribe bTypes.Topic
    sock.Connect <| address

  member this.StartListening onBlock =
    _executingTask <- Task.Factory.StartNew(this.WorkerThread onBlock, TaskCreationOptions.LongRunning)
    Task.CompletedTask

  member private this.WorkerThread (onBlock: OnBlock, ct: CancellationToken) () =
    while not <| ct.IsCancellationRequested do
      let m = sock.ReceiveMultipartMessage(3)
      let topic, body, sequence = (m.Item 0), (m.Item 1), (m.Item 2)
      if topic.Buffer = rawblockB then
        let b = Block.Parse(body.Buffer |> hex.EncodeData, Network.RegTest)
        onBlock b

  member this.IsConnected(t: TimeSpan) =
    let mutable m = null
    sock.TryReceiveMultipartMessage(t, &m, 3)

  interface IDisposable with
    member this.Dispose () =
      try
        if sock.IsDisposed then () else sock.Dispose()
        runtime.Dispose()
      with
      | ex ->
        logger.LogError $"Failed to dispose {nameof(ZmqClient)}. this should never happen: {ex}"

type ZmqBlockchainListener(opts: IOptions<NLoopOptions>,
                           zmqAddress,
                           loggerFactory,
                           getBlockchainClient,
                           actor,
                           cryptoCode,
                           rewindLimit: unit -> StartHeight) as this =
  inherit BlockchainListener(opts, loggerFactory, getBlockchainClient, cryptoCode, actor)
  let zmqClient = new ZmqClient(loggerFactory.CreateLogger<_>(), zmqAddress)
  let logger = loggerFactory.CreateLogger<ZmqBlockchainListener>()

  let [<Literal>] ConnectionRetryCount = 5
  let ConnectionBackoffInitialTime = TimeSpan.FromSeconds(0.5)

  member this.CheckConnection(ct: CancellationToken) = task {
    let mutable connectionEstablished = false
    let mutable i = 0
    while (i < ConnectionRetryCount && not <| connectionEstablished) do
      ct.ThrowIfCancellationRequested()
      let waitTime = (2. ** (float i)) * ConnectionBackoffInitialTime
      connectionEstablished <- zmqClient.IsConnected(waitTime)
      if not <| connectionEstablished then
        logger.LogInformation($"Failed to establish zmq connection to {cryptoCode}. Retrying in {waitTime.Seconds} seconds...")
        i <- i + 1
    return connectionEstablished
  }

  interface IHostedService with
    member this.StartAsync(cancellationToken) = unitTask {
      let onBlockSync = (fun b -> this.OnBlock(b, rewindLimit, cancellationToken).GetAwaiter().GetResult())
      do! zmqClient.StartListening(onBlockSync, cancellationToken)
    }
    member this.StopAsync(_cancellationToken) = unitTask {
      (zmqClient :> IDisposable).Dispose()
    }
