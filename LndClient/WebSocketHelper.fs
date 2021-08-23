namespace LndClient

open System
open System.Net.WebSockets
open System.Runtime.InteropServices
open System.Text
open System.Threading
open FSharp.Control.Tasks

/// Based on BTCPayServer
type WebSocketHelper(socket: WebSocket) =
  let _buffer =
    let b = Array.zeroCreate DefaultBufferSize
    ArraySegment<byte>(b, 0, b.Length)

  let utf8 = UTF8Encoding(false, true)
  member internal this.CloseSocketAndThrow(status: WebSocketCloseStatus, description: string, ct: CancellationToken) = unitTask {
    let arr = _buffer.Array
    if arr.Length <> DefaultBufferSize then
      Array.Resize(ref arr, DefaultBufferSize)
    do! socket.CloseAsync(status, description, ct)
    raise <| WebSocketException($"The socket has been closed ({status}: {description})")
  }

  member this.NextMessageAsync(ct: CancellationToken) =
    let mutable buffer = _buffer
    let array = _buffer.Array
    let originalSize = _buffer.Array.Length
    let mutable newSize = _buffer.Array.Length

    task {
      let mutable result: string = null
      while result |> String.IsNullOrEmpty do
        let! msg = socket.ReceiveAsync(buffer, ct)
        match msg.MessageType with
        | WebSocketMessageType.Close ->
          do! this.CloseSocketAndThrow(WebSocketCloseStatus.NormalClosure, "Close message received from the peer", ct)
        | WebSocketMessageType.Text ->
          ()
        | _ ->
          do! this.CloseSocketAndThrow(WebSocketCloseStatus.InvalidMessageType, "Only test is supported" , ct)

        if msg.EndOfMessage then
          buffer <- ArraySegment<byte>(array, 0, buffer.Offset + msg.Count)
          try
            let o = utf8.GetString(buffer.Array, 0, buffer.Count)
            if newSize <> originalSize then
              Array.Resize(ref array, originalSize)
            result <- o
          with
          | ex ->
            do! this.CloseSocketAndThrow(WebSocketCloseStatus.InvalidPayloadData, $"Invalid payload: {ex.Message}", ct)
        else
          if (buffer.Count - msg.Count <= 0) then
            newSize <- newSize * 2
            if (newSize > MaxBufferSize) then
              do! this.CloseSocketAndThrow(WebSocketCloseStatus.MessageTooBig, "Message is too big", ct)
            Array.Resize(ref array, newSize)
            buffer <- ArraySegment<byte>(array, buffer.Offset, newSize - buffer.Offset)
          buffer <- buffer.Slice(msg.Count, buffer.Count - msg.Count)
      return result
    }

  member this.Send(evt: string,
                   [<OptionalArgument>]ct: CancellationToken) =
    let bytes = utf8.GetBytes(evt)
    unitTask {
      use cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct)
      return! socket.SendAsync(ArraySegment<_>(bytes), WebSocketMessageType.Text, true, cts2.Token)
    }

  member this.DisposeAsync(ct) = unitVtask {
    try
      try
        return! socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing the socket", ct)
      with
      | _ -> ()
    finally
      try
        socket.Dispose()
      with
      | _ -> ()
  }
  interface IAsyncDisposable with
    member this.DisposeAsync() =
      this.DisposeAsync(CancellationToken.None)

open System.Threading.Tasks

[<AbstractClass>]
type NotificationSessionBase<'TEvent>() =
  abstract member NextEventAsync: ct: CancellationToken -> Task<'TEvent>
  member this.NextEvent(ct) =
    this.NextEventAsync(ct).GetAwaiter().GetResult()

