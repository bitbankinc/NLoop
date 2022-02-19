namespace NLoop.Server.SwapServerClient


type LoopSwapServerClient() =
  do ()

  interface ISwapServerClient with
    member this.CheckConnection(ct) =
      failwith "todo"
    member this.GetLoopInQuote(request, ct) = failwith "todo"
    member this.GetLoopInTerms(req, ct) = failwith "todo"
    member this.GetLoopOutQuote(request, ct) = failwith "todo"
    member this.GetLoopOutTerms(req, ct) = failwith "todo"
    member this.GetNodes(ct) = failwith "todo"
    member this.ListenToSwapTx(swapId, onSwapTx, ct) = failwith "todo"
    member this.LoopIn(request, ct) = failwith "todo"
    member this.LoopOut(request, ct) = failwith "todo"
