[<AutoOpen>]
module internal Helpers

[<RequireQualifiedAccess>]
module Result =
  let deref =
    function
      | Ok r -> r
      | Error e -> failwithf "%A" e

