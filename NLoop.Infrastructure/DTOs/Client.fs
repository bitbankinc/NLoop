namespace NLoop.Infrastructure.DTOs


type SwapType =
  | LoopIn
  | LoopOut

  with
  member this.Name =
    match this with
    | LoopOut -> "LOOP_OUT"
    | LoopIn -> "LOOP_IN"

