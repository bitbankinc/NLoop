[<AutoOpen>]
module internal Helpers

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
    | Error e -> failwithf "Assertion Failed! Must be Ok but it was (%A)" e
  let inline isError (r: Result<_, _>) =
    match r with
    | Ok ok -> failwithf "Assertion Failed! Must be Error but it was (%A)" ok
    | Error _e -> ()
