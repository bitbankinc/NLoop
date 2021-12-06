[<AutoOpen>]
module internal TestUtils

open Xunit

[<RequireQualifiedAccess>]
module Result =
  let deref =
    function
      | Ok r -> r
      | Error e -> failwithf "%A" e


module Assertion =
  let inline isOk (r: Result<_, _>) =
    match r with
    | Ok _ -> ()
    | Error e -> failwithf $"Assertion Failed! Must be Ok but it was (%A{e})"
  let inline isError (r: Result<_, _>) =
    match r with
    | Ok ok -> failwithf $"Assertion Failed! Must be Error but it was (%A{ok})"
    | Error _e -> ()

  let isErrorOf<'TError>(r: Result<_, _>) =
    match r with
    | Ok ok -> failwithf $"Assertion Failed! Must be Error but it was (%A{ok})"
    | Error e ->
      let actualT = e.GetType()
      let expectedT = typeof<'TError>
      if actualT = expectedT then () else
      failwith $"Assertion Failed! expected error type: {expectedT}. actual: {actualT}."

  let inline isSame(expected: Result<'T, 'E>, actual: Result<'T, 'E>) =
    match expected, actual with
    | Ok e, Ok a ->
      Assert.Equal<'T>(e, a)
    | Error e, Error a ->
      Assert.Equal<'E>(e, a)
    | e, a ->
      failwith $"expected: {e}\nactual: {a}"
