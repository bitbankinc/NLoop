[<RequireQualifiedAccess>]
module internal NLoop.Server.Labels

  open NLoop.Domain

  /// Used as a prefix to separate labels that are created by loopd from those
  /// created by users.
  let [<Literal>] reservedPrefix = "[reserved]"
  let [<Literal>] private autoOutTag = "autoloop-out"
  let [<Literal>] private autoInTag = "autoloop-in"

  let [<Literal>] MaxLength = 500

  let autoLoopLabel(category: Swap.Category) =
    match category with
    | Swap.Category.Out ->
      $"{reservedPrefix}: {autoOutTag}"
    | Swap.Category.In ->
      $"{reservedPrefix}: {autoInTag}"

  let autoIn = autoLoopLabel(Swap.Category.In)
  let autoOut = autoLoopLabel(Swap.Category.Out)
