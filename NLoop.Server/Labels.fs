[<RequireQualifiedAccess>]
module internal NLoop.Server.Labels

  open NLoop.Domain

  /// Used as a prefix to separate labels that are created by loopd from those
  /// created by users.
  let [<Literal>] reserved = "[reserved]"
  let [<Literal>] autoOut = "autoloop-out"
  let [<Literal>] autoIn = "autoloop-in"

  let autoLoopLabel(category: Swap.Category) =
    match category with
    | Swap.Category.Out ->
      $"{reserved}: {autoOut}"
    | Swap.Category.In ->
      $"{reserved}: {autoIn}"
