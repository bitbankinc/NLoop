namespace NLoop.Infrastructure

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading
open DBTrie.Storage.Cache
open Microsoft.Extensions.Options
open FSharp.Control.Tasks
open DBTrie
open NBitcoin
open NBitcoin.Crypto
open NLoop.Infrastructure.Utils

type RepositorySerializer() =
  do ()
  member this.ConfigureSerializer(opts) =
    failwith "TODO"

module private DBKeys =
  [<Literal>]
  let HashToKey = "hk"

  [<Literal>]
  let HashToPreimage = "hp"

type Repository(conf: IOptions<NLoopServerConfig>) =
  let dbPath = conf.Value.DBPath
  let openEngine() = task {
    return! DBTrieEngine.OpenFromFolder(dbPath)
  }

  let pageSize = 8192
  let serializerOpts = JsonSerializerOptions()
  let repoSerializer = RepositorySerializer()

  do
    if dbPath |> Directory.Exists |> not then
      Directory.CreateDirectory(dbPath) |> ignore
    repoSerializer.ConfigureSerializer(serializerOpts)
      ()
  let engine = openEngine().GetAwaiter().GetResult()
  do
    engine.ConfigurePagePool(PagePool(pageSize, 50 * 1000 * 1000 / pageSize))

  member this.SetPrivateKey(key: Key, [<O;DefaultParameterValue(null)>]ct: CancellationToken) =
    if (key |> box |> isNull) then raise <| ArgumentNullException(nameof key) else
    task {
      use! tx = engine.OpenTransaction(ct)
      let k = ReadOnlyMemory(key.PubKey.Hash.ToBytes())
      let v = ReadOnlyMemory(key.ToBytes())
      let! _ = tx.GetTable(DBKeys.HashToKey).Insert(k, v)
      do! tx.Commit()
    }

  member this.GetPrivateKey(pubKeyHash: KeyId, [<O;DefaultParameterValue(null)>]ct: CancellationToken) =
    if (pubKeyHash |> box |> isNull) then raise <| ArgumentNullException(nameof pubKeyHash) else
    task {
      use! tx = engine.OpenTransaction(ct)
      let k = pubKeyHash.ToBytes() |> ReadOnlyMemory
      let! row = tx.GetTable(DBKeys.HashToKey).Get(k)
      return! row.ReadValue()
    }

  member this.SetPreimage(preimage: byte[], [<O;DefaultParameterValue(null)>]ct: CancellationToken) =
    if (preimage |> box |> isNull) then raise <| ArgumentNullException(nameof preimage) else
    if (preimage.Length <> 32) then raise <| ArgumentException($"length of {nameof preimage} must be 32") else
    task {
      use! tx = engine.OpenTransaction(ct)
      let k = ReadOnlyMemory(preimage |> Hashes.Hash160 |> fun d -> d.ToBytes())
      let v = ReadOnlyMemory(preimage)
      let! _ = tx.GetTable(DBKeys.HashToKey).Insert(k, v)
      do! tx.Commit()
    }

  member this.GetPreimage(preimageHash: uint160, [<O;DefaultParameterValue(null)>]ct: CancellationToken) =
    if (preimageHash |> box |> isNull) then raise <| ArgumentNullException(nameof preimageHash) else
    task {
      use! tx = engine.OpenTransaction(ct)
      let k = preimageHash.ToBytes() |> ReadOnlyMemory
      let! row = tx.GetTable(DBKeys.HashToKey).Get(k)
      return! row.ReadValue()
    }
