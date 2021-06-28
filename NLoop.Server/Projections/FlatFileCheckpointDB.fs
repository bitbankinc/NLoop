namespace NLoop.Server.Projections

open System
open System.IO

open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open DBTrie
open DBTrie.Storage.Cache
open FSharp.Control.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open NLoop.Domain
open NLoop.Server

module private DBKeys =
  [<Literal>]
  let Checkpoints = "c"

  [<Literal>]
  let SwapState = "ss"

type ICheckpointDB =
  abstract member GetSwapStateCheckpoint: ct: CancellationToken -> ValueTask<int64 voption>
  abstract member SetSwapStateCheckpoint: checkpoint: int64 * ct:CancellationToken -> ValueTask

type FlatFileCheckpointDB(opts: IOptions<NLoopOptions>, logger: ILogger<FlatFileCheckpointDB>) =
  let openEngine(dbPath) = task {
    return! DBTrieEngine.OpenFromFolder(dbPath)
  }

  let mutable engine = null
  let pageSize = 8192
  let startAsync(_stoppingToken) = unitTask {
    let dbPath = Path.Join(opts.Value.DBPath, "checkpoints")
    if (not <| Directory.Exists(dbPath)) then
      Directory.CreateDirectory(dbPath) |> ignore
    let! e = openEngine(dbPath)
    engine <- e
    engine.ConfigurePagePool(PagePool(pageSize, 50 * 1000 * 1000 / pageSize))
  }
  do
    startAsync(CancellationToken.None).GetAwaiter().GetResult() |> ignore

  interface ICheckpointDB with
    member this.GetSwapStateCheckpoint(ct) = vtask {
      use! tx = engine.OpenTransaction(ct)
      let! row = tx.GetTable(DBKeys.Checkpoints).Get(DBKeys.SwapState)
      if (row |> isNull) then return ValueNone else
      let! s = row.ReadValueString()
      match Int64.TryParse s with
      | true, v -> return ValueSome v
      | false, _ -> return ValueNone
    }

    member this.SetSwapStateCheckpoint(checkpoint, [<O;DefaultParameterValue(null)>]ct: CancellationToken) = unitVtask {
      use! tx = engine.OpenTransaction(ct)
      let c = checkpoint.ToString()
      let! _ = tx.GetTable(DBKeys.Checkpoints).Insert(DBKeys.SwapState, c)
      do! tx.Commit()
      return()
    }

  interface IDisposable with
    member this.Dispose() =
      if (engine |> isNull |> not) then
        engine.DisposeAsync().GetAwaiter().GetResult()
