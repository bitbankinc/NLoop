namespace NLoop.Domain

type O = OptionalArgumentAttribute
open System
open System.Runtime.CompilerServices

// See https://devblogs.microsoft.com/pfxteam/await-anything/ for more details on awaitables/awaiters.

[<AutoOpen>]
module Helpers =
  type Async with
      static member inline AwaitCSharpAwaitable< ^TAwaitable, ^TAwaiter, ^TResult when
                                                 ^TAwaiter :> INotifyCompletion and
                                                 ^TAwaitable : (member GetAwaiter : unit -> ^TAwaiter) and
                                                 ^TAwaiter : (member IsCompleted : bool) and
                                                 ^TAwaiter : (member GetResult : unit -> ^TResult)>
                                                 (awaitable : ^TAwaitable) : Async< ^TResult> =

          Async.FromContinuations(fun (sk,ek,_) ->
              let awaiter = (^TAwaitable : (member GetAwaiter : unit -> ^TAwaiter) awaitable)
              let oncompleted () =
                  let result =
                      try Ok (^TAwaiter : (member GetResult : unit -> ^TResult) awaiter)
                      with e -> Error e

                  match result with
                  | Ok t -> sk t
                  | Error e -> ek e

              if (^TAwaiter : (member IsCompleted : bool) awaiter) then
                  oncompleted()
              else
                  // NB does not flow the execution context
                  awaiter.OnCompleted(Action oncompleted)
          )
