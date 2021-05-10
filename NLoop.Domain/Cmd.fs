namespace NLoop.Domain

open System.Threading.Tasks

(*
type Dispatch<'msg> = 'msg -> unit
type Sub<'msg> = Dispatch<'msg> -> unit
type Cmd<'msg> = Sub<'msg> list

/// Elmish-like Cmd module
[<RequireQualifiedAccess>]
module Cmd =

  /// Execute  the commands using the supplied dispatcher
  let internal exec onError (dispatch: Dispatch<'msg>) (cmd: Cmd<'msg>) =
    cmd |> List.iter(fun call -> try call dispatch with ex -> onError ex)
  let none: Cmd<'msg> = []

  let map (f: 'a -> 'msg) (cmd: Cmd<'a>): Cmd<'msg> =
    cmd |> List.map(fun g -> (fun dispatch -> f >> dispatch) >> g)

  let batch (cmds: #seq<Cmd<'msg>>) : Cmd<'msg> =
    cmds |> List.concat

  let ofSub (sub: Sub<'msg>): Cmd<'msg> =
    [sub]

  module OfFunc =
    /// Command to evaluate a simple function and map the result
    /// into success or error (of exception)
    let either (task: 'a -> _) (arg: 'a) (ofSuccess: _ -> 'msg) (ofError: _ -> 'msg): Cmd<'msg> =
      let bind dispatch =
        try
          task arg
          |> (ofSuccess >> dispatch)
        with x ->
          ()
      [bind]

    /// Command to evaluate a simple function and map the success to a message
    /// Discarding any possible error
    let perform (task : 'a -> _) (arg: 'a) (ofSuccess: _ -> 'msg) =
      let bind dispatch =
        try
          task arg
          |> (ofSuccess >> dispatch)
        with x -> ()
      [bind]

    let attempt(task: 'a -> unit) (arg: 'a) (ofError: _ -> 'msg): Cmd<'msg>  =
      let bind dispatch =
        try
          task arg
        with x -> x |> (ofError >> dispatch)
      [bind]

    let result (msg: 'msg) : Cmd<'msg> =
      [fun dispatch -> dispatch msg]

  module OfAsyncWith =
    let either
      (start: Async<unit> -> unit)
      (task: 'a -> Async<_>)
      (arg: 'a)
      (ofSuccess: _ -> 'msg)
      (ofError: _ -> 'msg): Cmd<'msg> =
      let bind dispatch =
        async {
          let! r = task arg |> Async.Catch
          dispatch(r |> function | Choice1Of2 x -> ofSuccess x | Choice2Of2 x -> ofError x)
        }
      [bind >> start]

    let perform
      (start: Async<unit> -> unit)
      (task: 'a -> Async<_>)
      (arg: 'a)
      (ofSuccess: _ -> 'msg): Cmd<'msg> =
      let bind dispatch =
        async {
          match! task arg |> Async.Catch with
          | Choice1Of2 x -> dispatch(ofSuccess x)
          | _ -> ()
        }
      [bind >> start]

    let attempt
      (start: Async<unit> -> unit)
      (task: 'a -> Async<_>)
      (arg: 'a)
      (ofError: _ -> 'msg): Cmd<'msg> =
      let bind dispatch =
        async {
          match! task arg |> Async.Catch with
          | Choice1Of2 _ -> ()
          | Choice2Of2 x -> dispatch(ofError x)
        }
      [bind >> start]

    let result
      (start: Async<unit> -> unit)
      (task : Async<'msg>): Cmd<'msg> =
      let bind dispatch =
        async {
          let! r = task
          dispatch r
        }
      [bind >> start]


  module OfAsync =
    let inline start x = Async.Start x

    let inline either
      (task: 'a -> Async<_>)
      (arg: 'a)
      ofSuccess
      ofError: Cmd<'msg> =
      OfAsyncWith.either start task arg ofSuccess ofError

    let inline perform
      task
      arg
      ofSuccess: Cmd<'msg> =
      OfAsyncWith.perform start task arg ofSuccess
    let inline attempt
      task
      arg
      ofError: Cmd<'msg> =
      OfAsyncWith.attempt start task arg ofError
    let inline result
      (task: Async<'msg>) : Cmd<'msg> =
      OfAsyncWith.result start task

  open System.Threading.Tasks
  module OfTask =
    let inline either
      (task: 'a -> Task<_>)
      (arg: 'a)
      (ofSuccess: _ -> 'msg)
      (ofError: _ -> 'msg): Cmd<'msg>
      =
      OfAsync.either (task >> Async.AwaitTask) (arg) ofSuccess ofError
    let inline eitherUnit
      (task: 'a -> Task)
      (arg: 'a)
      (ofSuccess: _ -> 'msg)
      (ofError: _ -> 'msg): Cmd<'msg>
      =
      OfAsync.either (task >> Async.AwaitTask) (arg) ofSuccess ofError

    let inline perform
      (task: 'a -> Task<_>)
      (arg: 'a)
      (ofSuccess: _ -> 'msg): Cmd<'msg> =
      OfAsync.perform (task >> Async.AwaitTask) arg ofSuccess

    let inline attempt
      (task: 'a -> Task<_>)
      (arg: 'a)
      (ofError: _ -> 'msg): Cmd<'msg> =
      OfAsync.attempt (task >> Async.AwaitTask) arg ofError

    let inline result
      (task: Task<_>)
      : Cmd<'msg> =
      OfAsync.result (task |> Async.AwaitTask)

    let inline ofMsg (msg:'msg) : Cmd<'msg> =
      OfFunc.result msg
*)
