namespace NLoop.Domain

open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Control.Tasks
open Microsoft.Extensions.Logging


[<AbstractClass>]
type Actor<'TState, 'TMsg, 'TEvent, 'TError>(aggregate: Aggregate<'TState, 'TMsg, 'TEvent, 'TError>, log: ILogger, ?capacity: int) as this =
    let mutable disposed = false
    let capacity = defaultArg capacity 600
    let communicationChannel =
        let options = BoundedChannelOptions(capacity)
        options.SingleReader <- true
        options.SingleWriter <- false
        Channel.CreateBounded<'TMsg * TaskCompletionSource<unit> option>(options)

    let mutable _s = aggregate.Zero
    let lockObj = obj()
    let startAsync() = task {
        let mutable nonFinished = true
        while nonFinished && (not disposed) do
            let! cont = communicationChannel.Reader.WaitToReadAsync()
            nonFinished <- cont
            if nonFinished && (not disposed) then
                match (communicationChannel.Reader.TryRead()) with
                | true, (cmd, maybeTcs)->
                    let msg = sprintf "read cmd '%A from communication channel" (cmd)
                    log.LogTrace(msg)
                    match! aggregate.Exec this.State cmd with
                    | Ok events ->
                        let _ =
                          let msg = sprintf "Successfully executed command (%A) and got events %A" cmd events
                          log.LogTrace(msg)
                        for e in events do
                          let nextState, nextMsg = aggregate.Apply this.State e
                          match nextMsg with
                          | Some m ->
                            do! this.Put(m)
                          | None -> ()
                          this.State <- nextState
                        maybeTcs |> Option.iter(fun tcs -> tcs.SetResult())
                        for e in events do
                            do! this.PublishEvent e
                    | Error ex ->
                        log.LogTrace(sprintf "failed to execute command and got error %A" ex)
                        ex |> this.HandleError |> ignore
                        maybeTcs |> Option.iter(fun tcs -> tcs.SetException(exn(sprintf "%A" ex)))
                | false, _ ->
                    ()
        log.LogInformation "disposing actor"
        return ()
    }
    do
        startAsync() |> ignore
    member this.State
      with get () = _s
      and private set (s) =
        lock lockObj <| fun () ->
          _s <- s
    abstract member PublishEvent: evt: 'TEvent -> Task
    abstract member HandleError: error: 'TError -> Task

    member this.Put(msg: 'TMsg) =
        communicationChannel.Writer.WriteAsync((msg, None))

    member this.PutAndWaitProcess(msg: 'TMsg) =
        let tcs = TaskCompletionSource<unit>()
        communicationChannel.Writer.WriteAsync((msg, Some(tcs))) |> ignore
        tcs.Task :> Task

    member this.Dispose() =
        disposed <- true

