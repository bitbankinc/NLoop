namespace NLoop.Domain

open System
open System.Collections.Concurrent
open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Control.Tasks
open Microsoft.Extensions.Logging


[<AbstractClass>]
type Actor<'TState, 'TMsg, 'TEvent>(aggregate: Aggregate<'TState, 'TMsg, 'TEvent>, log: ILogger, ?capacity: int) as this =
    let mutable disposed = false
    let capacity = defaultArg capacity 600
    let communicationChannel =
        let options = BoundedChannelOptions(capacity)
        options.SingleReader <- true
        options.SingleWriter <- false
        Channel.CreateBounded<'TMsg * TaskCompletionSource<unit> option>(options)

    let mutable _s = aggregate.Zero
    let _tasks = ConcurrentBag<Task<_>>()
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
                    let! events = aggregate.Exec this.State cmd
                    for e in events do
                        let nextState, cmd = aggregate.Apply this.State e
                        cmd |> Cmd.exec (this.HandleError) (this.Put >> ignore)
                        this.State <- nextState
                    maybeTcs |> Option.iter(fun tcs -> tcs.SetResult())
                    for e in events do
                        do! this.PublishEvent e
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
    abstract member HandleError: error: exn -> unit

    member this.Put(msg: 'TMsg) =
        communicationChannel.Writer.WriteAsync((msg, None))

    member this.PutAndWaitProcess(msg: 'TMsg) =
        let tcs = TaskCompletionSource<unit>()
        communicationChannel.Writer.WriteAsync((msg, Some(tcs))) |> ignore
        tcs.Task :> Task

    member this.Dispose() =
        disposed <- true

