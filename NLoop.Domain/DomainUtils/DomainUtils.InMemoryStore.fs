namespace NLoop.Domain.Utils

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading.Tasks
open FSharp.Control.Tasks

[<RequireQualifiedAccess>]
module InMemoryStore =

  let eventStore() =
    let streams: ConcurrentDictionary<StreamId, Stack<SerializedRecordedEvent>> =
      ConcurrentDictionary()
    let readLast: StreamId -> Task<Result<_,_>>  =
      fun (streamId: StreamId)-> task {
        match streams.TryGetValue streamId with
        | false, _ ->
          return None |> Ok
        | true, stack ->
          match stack.TryPeek() with
          | true, last ->
            return last |> Some |> Ok
          | false, _ ->
            return failwith "unreachable"
      }

    let readStream : StreamId -> Task<_> =
      fun (streamId: StreamId) -> task {
        try
          match streams.TryGetValue streamId with
          | true, v ->
            return v :> seq<_> |> Seq.rev |> Ok
          | false, _ ->
            return Seq.empty |> Ok
        with
        | ex ->
            return $"Failed to read stream! \n %A{ex}" |> StoreError |> Error
      }

    let writeStream : _ -> _ -> StreamId -> Task<_> =
      fun (expectedVersion: ExpectedVersionUnion) (events: SerializedEvent list) (streamId: StreamId) ->
        let r =
          let doesStreamExists, stack = streams.TryGetValue(streamId)
          match expectedVersion with
          | NoStream when doesStreamExists ->
            "Expected No Stream but there was some"
            |> StoreError |> Error
          | StreamExists when not doesStreamExists ->
            "Expected Stream but there was none"
            |> StoreError |> Error
          | Specific _ when not doesStreamExists ->
            "Expected Stream but there was none"
            |> StoreError |> Error
          | Specific i when doesStreamExists && (stack.Count |> int64 <> i) ->
            $"Next event version is {stack.Count + 1}. But It did not match to the one specified ({i})"
            |> StoreError |> Error
          | _ ->
            try
              let updateStack = fun (v: Stack<_>) ->
                events
                |> List.mapi(fun i e ->
                  {
                    SerializedRecordedEvent.Data = e.Data
                    Id = Guid.NewGuid() |> EventId
                    Type = e.Type
                    EventNumber = (v.Count + 1 + i) |> uint64 |> EventNumber.Create
                    CreatedDate = DateTime.UtcNow |> UnixDateTime.Create |> function Ok e -> e | Error e -> failwith $"Unreachable! {e}"
                    Meta = e.Meta
                  })
                |> List.iter(v.Push)
                v
              streams.AddOrUpdate(streamId, (fun _ -> Stack() |> updateStack), fun _streamId -> updateStack)
              |> ignore
              Ok ()
            with
            | ex ->
              ($"Failed to writeStream %A{ex}") |> StoreError |> Error
        r |> Task.FromResult
    {
      Store.ReadLast = readLast
      ReadStream = readStream
      WriteStream = writeStream
    }
