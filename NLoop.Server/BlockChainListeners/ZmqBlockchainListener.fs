namespace NLoop.Server

open System
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
open NLoop.Server.Options
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

type private OnBlock = SupportedCryptoCode * Block -> unit
type ZmqClient(cc: SupportedCryptoCode,
               opts: IOptions<NLoopOptions>,
               onBlock: OnBlock,
               logger: ILogger<ZmqClient>,
               ?ct: CancellationToken) as this =
  let sock = new SubscriberSocket()
  let runtime = new NetMQRuntime()
  let ct = defaultArg ct CancellationToken.None

  do
    Task.Factory.StartNew(this.WorkerThread, TaskCreationOptions.LongRunning)
    |> ignore

  member private this.WorkerThread() =
    for bTypes in seq [RawBlock; HashBlock] do
      sock.Subscribe bTypes.Topic
    sock.Connect <| opts.Value.ChainOptions.[cc].GetZmqAddress()
    while not <| ct.IsCancellationRequested do
      let m = sock.ReceiveMultipartMessage(3)
      let topic, body, sequence = (m.Item 0), (m.Item 1), (m.Item 2)
      if topic.Buffer = rawblockB then
        let b = Block.Parse(body.Buffer |> hex.EncodeData, Network.RegTest)
        onBlock(cc, b)

  interface IDisposable with
    member this.Dispose () =
      try
        if sock.IsDisposed then () else sock.Dispose()
        runtime.Dispose()
      with
      | ex ->
        logger.LogError $"Failed to dispose {nameof(ZmqClient)}. this should never happen: {ex}"

type ZmqBlockchainListener(opts: IOptions<NLoopOptions>, loggerFactory, getBlockchainClient, actor) =
  inherit BlockchainListener(opts, loggerFactory, getBlockchainClient, actor)
  let zmqClients = ResizeArray()
  let logger = loggerFactory.CreateLogger<ZmqBlockchainListener>()

  interface IHostedService with
    member this.StartAsync(cancellationToken) = unitTask {
      opts.Value.OnChainCrypto
      |> Seq.iter(fun cc ->
        let onBlockSync = (fun (cc, b) -> this.OnBlock(cc, b, cancellationToken).GetAwaiter().GetResult())
        new ZmqClient(cc, opts, onBlockSync, loggerFactory.CreateLogger<_>(), cancellationToken)
        |> zmqClients.Add
      )
    }
    member this.StopAsync(_cancellationToken) = unitTask {
      for c in zmqClients do
        try
          (c :> IDisposable).Dispose()
        with
        | ex ->
          logger.LogDebug $"socket already closed"
    }
