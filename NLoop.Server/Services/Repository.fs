﻿namespace NLoop.Server

open System
open System.Collections.Generic
open System.IO
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open DBTrie.Storage.Cache
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Options
open FSharp.Control.Tasks
open DBTrie
open NBitcoin
open NBitcoin.Crypto
open NLoop.Server.DTOs
open NLoop.Server.Utils

module private DBKeys =
  [<Literal>]
  let HashToKey = "hk"

  [<Literal>]
  let HashToPreimage = "hp"

  [<Literal>]
  let idToLoopOutSwap = "io"

  [<Literal>]
  let idToLoopInSwap = "ii"

type IRepository =
  abstract member SetPrivateKey: key: Key -> Task
  abstract member GetPrivateKey: keyId: KeyId -> Task<Key option>
  abstract member SetPreimage: preimage: byte[] -> Task
  abstract member GetPreimage: preimageHashHash: uint160 -> Task<byte[] option>
  abstract member SetLoopOut: loopOut: LoopOut -> Task
  abstract member GetLoopOut: id: string -> Task<LoopOut option>
  abstract member SetLoopIn: loopIn: LoopIn -> Task
  abstract member GetLoopIn: id: string -> Task<LoopIn option>
  abstract member JsonOpts: JsonSerializerOptions

[<Sealed;AbstractClass;Extension>]
type IRepositoryExtensions() =
  [<Extension>]
  static member NewPrivateKey(this: IRepository) = task {
    use k = new Key()
    do! this.SetPrivateKey(k)
    return k
  }

  [<Extension>]
  static member NewPreimage(this: IRepository) = task {
    let preimage = RandomUtils.GetBytes(32)
    do! this.SetPreimage(preimage)
    return preimage
  }


type Repository(conf: IOptions<NLoopOptions>, crypto: SupportedCryptoCode) =
  let dbPath = conf.Value.DBPath
  let openEngine() = task {
    return! DBTrieEngine.OpenFromFolder(dbPath)
  }

  let serializerOpts = JsonSerializerOptions()
  let jsonOpts = JsonSerializerOptions()
  let pageSize = 8192

  let engine = openEngine().GetAwaiter().GetResult()
  do
    if dbPath |> Directory.Exists |> not then
      Directory.CreateDirectory(dbPath) |> ignore
      ()
    engine.ConfigurePagePool(PagePool(pageSize, 50 * 1000 * 1000 / pageSize))

  member this.SetPrivateKey(key: Key, [<O;DefaultParameterValue(null)>]ct: CancellationToken) =
    if (key |> box |> isNull) then raise <| ArgumentNullException(nameof key) else
    unitTask {
      use! tx = engine.OpenTransaction(ct)
      let k = ReadOnlyMemory(key.PubKey.Hash.ToBytes())
      let v = ReadOnlyMemory(key.ToBytes())
      let! _ = tx.GetTable(DBKeys.HashToKey).Insert(k, v)
      do! tx.Commit()
    }
  member this.GetPrivateKey(pubKeyHash: KeyId, [<O;DefaultParameterValue(null)>]ct: CancellationToken) =
    if (pubKeyHash |> box |> isNull) then raise <| ArgumentNullException(nameof pubKeyHash) else
    task {
      try
        use! tx = engine.OpenTransaction(ct)
        let k = pubKeyHash.ToBytes() |> ReadOnlyMemory
        let! row = tx.GetTable(DBKeys.HashToKey).Get(k)
        let! b = row.ReadValue()
        return new Key(b.ToArray()) |> Some
      with
      | e -> return None
    }
  member this.SetPreimage(preimage: byte[], [<O;DefaultParameterValue(null)>]ct: CancellationToken) =
    if (preimage |> box |> isNull) then raise <| ArgumentNullException(nameof preimage) else
    if (preimage.Length <> 32) then raise <| ArgumentException($"length of {nameof preimage} must be 32") else
    unitTask {
      use! tx = engine.OpenTransaction(ct)
      let k = ReadOnlyMemory(preimage |> Hashes.Hash160 |> fun d -> d.ToBytes())
      let v = ReadOnlyMemory(preimage)
      let! _ = tx.GetTable(DBKeys.HashToKey).Insert(k, v)
      do! tx.Commit()
    }

  member this.GetPreimage(preimageHash: uint160, [<O;DefaultParameterValue(null)>]ct: CancellationToken) =
    if (preimageHash |> box |> isNull) then raise <| ArgumentNullException(nameof preimageHash) else
    task {
      try
        use! tx = engine.OpenTransaction(ct)
        let k = preimageHash.ToBytes() |> ReadOnlyMemory
        let! row = tx.GetTable(DBKeys.HashToKey).Get(k)
        let! x = row.ReadValue()
        return x.ToArray() |> Some
      with
      | _ -> return None
    }

    member this.SetLoopOut(loopOut: LoopOut) =
      if (loopOut |> box |> isNull) then raise <| ArgumentNullException(nameof loopOut) else
      unitTask {
        use! tx = engine.OpenTransaction()
        let v = JsonSerializer.Serialize(loopOut, jsonOpts)
        let! _ = tx.GetTable(DBKeys.idToLoopOutSwap).Insert(loopOut.Id, v)
        do! tx.Commit()
      }
    member this.GetLoopOut(id: string) =
      if (id |> box |> isNull) then raise <| ArgumentNullException(nameof id) else
      task {
        try
          use! tx = engine.OpenTransaction()
          let! row = tx.GetTable(DBKeys.HashToKey).Get(id)
          let! x = row.ReadValue()
          return x.ToArray() |> LoopOut.FromBytes |> Some
        with
        | e -> return None
      }
    member this.SetLoopIn(loopIn: LoopIn) =
      if (loopIn |> box |> isNull) then raise <| ArgumentNullException(nameof loopIn) else
      unitTask {
        use! tx = engine.OpenTransaction()
        let v = ReadOnlyMemory(loopIn.ToBytes())
        let! _ = tx.GetTable(DBKeys.idToLoopOutSwap).Insert(loopIn.Id, v)
        do! tx.Commit()
      }
    member this.GetLoopIn(id: string) =
      if (id |> box |> isNull) then raise <| ArgumentNullException(nameof id) else
      task {
        try
          use! tx = engine.OpenTransaction()
          let! row = tx.GetTable(DBKeys.HashToKey).Get(id)
          let! x = row.ReadValue()
          return x.ToArray() |> LoopIn.FromBytes |> Some
        with
        | e -> return None
      }

    interface IRepository with
      member this.GetLoopIn(id) = this.GetLoopIn(id)
      member this.GetLoopOut(id) = this.GetLoopOut(id)
      member this.GetPreimage(preimageHashHash) = this.GetPreimage(preimageHashHash)
      member this.GetPrivateKey(keyId) = this.GetPrivateKey(keyId)
      member this.SetLoopIn(loopIn) = this.SetLoopIn(loopIn)
      member this.SetLoopOut(loopOut) = this.SetLoopOut(loopOut)
      member this.SetPreimage(preimage) = this.SetPreimage(preimage)
      member this.SetPrivateKey(key) = this.SetPrivateKey(key)
      member val JsonOpts = jsonOpts with get


type IRepositoryProvider =
  abstract member TryGetRepository: crypto: SupportedCryptoCode -> IRepository option

[<Extension;AbstractClass;Sealed>]
type IRepositoryProviderExtensions()=
  [<Extension>]
  static member GetRepository(this: IRepositoryProvider, crypto: SupportedCryptoCode): IRepository =
    match this.TryGetRepository crypto with
    | Some x -> x
    | None ->
      raise <| InvalidDataException($"cryptocode {crypto} not supported")

  [<Extension>]
  static member GetRepository(this: IRepositoryProvider, cryptoCode: string): IRepository =
    this.GetRepository(SupportedCryptoCode.Parse(cryptoCode))

type RepositoryProvider(opts: IOptions<NLoopOptions>) =
  inherit BackgroundService()
  let repositories = Dictionary<SupportedCryptoCode, IRepository>()
  let startCompletion = TaskCompletionSource<bool>()
  do
    for on in opts.Value.OnChainCrypto do
      repositories.Add(on, Repository(opts, on))

  let openEngine(dbPath) = task {
    return! DBTrieEngine.OpenFromFolder(dbPath)
  }

  let mutable engine = null
  let pageSize = 8192

  member this.StartCompletion = startCompletion.Task

  interface IRepositoryProvider with
    member this.TryGetRepository(crypto: SupportedCryptoCode): IRepository option =
      match repositories.TryGetValue(crypto) with
      | true, v -> Some v
      | false, _ -> None

  override this.ExecuteAsync(stoppingToken) = unitTask {
      try
        let dir = Path.Combine(opts.Value.DataDir, "db")
        if (not <| Directory.Exists(dir)) then
          Directory.CreateDirectory(dir) |> ignore
        let! e = openEngine(dir)
        engine <- e
        engine.ConfigurePagePool(PagePool(pageSize, 50 * 1000 * 1000 / pageSize))
        return failwith "todo"
      with
      | x ->
        startCompletion.TrySetCanceled() |> ignore
        raise <| x
    }
