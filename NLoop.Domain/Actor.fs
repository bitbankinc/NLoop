namespace NLoop.Domain

open System.Threading.Channels
open System.Threading.Tasks
open FSharp.Control.Tasks
open Microsoft.Extensions.Logging


[<AbstractClass>]
type Actor<'TState, 'TCommand, 'TEvent, 'TError>(aggregate: Aggregate<'TState, 'TCommand, 'TEvent, 'TError>, log: ILogger, ?capacity: int) as this =
    let mutable disposed = false
    let capacity = defaultArg capacity 600
    let communicationChannel =
        let options = BoundedChannelOptions(capacity)
        options.SingleReader <- true
        options.SingleWriter <- false
        Channel.CreateBounded<'TCommand * TaskCompletionSource<unit> option>(options)

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
                        let msg = sprintf "Successfully executed command (%A) and got events %A" cmd events
                        log.LogTrace(msg)
                        this.State <- events |> List.fold aggregate.Apply this.State
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
    member val State = aggregate.Zero with get, set
    abstract member PublishEvent: e: 'TEvent -> Task
    abstract member HandleError: 'TError -> Task

    member this.Put(cmd: 'TCommand) = unitTask {
            do! communicationChannel.Writer.WriteAsync((cmd, None))
        }

    member this.PutAndWaitProcess(cmd: 'TCommand) =
        let tcs = TaskCompletionSource<unit>()
        communicationChannel.Writer.WriteAsync((cmd, Some(tcs))) |> ignore
        tcs.Task :> Task

    member this.Dispose() =
        disposed <- true
        ()
