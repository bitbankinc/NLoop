namespace FSharp.Control.WebSockets


open System
open System.Threading
open System.Threading.Tasks
open System.Runtime.ExceptionServices

type Async =
  static member AwaitTaskCancellation(f: CancellationToken -> Task) : Async<unit> =
    async.Bind(Async.CancellationToken, f >> Async.AwaitTask)

  static member AwaitTaskWithCancellation(f: CancellationToken -> Task<'a>) : Async<'a> =
    async.Bind(Async.CancellationToken, f >> Async.AwaitTask)

module Stream =
  open System
  open Microsoft

  type IO.MemoryStream with
    static member UTF8toMemoryStream (text: string) =
      let b = Text.Encoding.UTF8.GetBytes(text)
      let m =  new IO.MemoryStream(b)
      m


    static member ToUTF8String (stream : IO.MemoryStream) =
      stream.Seek(0L, IO.SeekOrigin.Begin) |> ignore //ensure start of stream
      stream.ToArray()
      |> Text.Encoding.UTF8.GetString
      |> fun s -> s.TrimEnd(char 0)

    member stream.ToUTF8String () =
      stream |> System.IO.MemoryStream.ToUTF8String


module WebSocket =
  open Stream
  open System
  open System.Net.WebSockets

  [<Literal>]
  let DefaultBufferSize  : int = 16384

  let isWebsocketOpen (socket : WebSocket) =
    socket.State = WebSocketState.Open

  let asyncReceive (websocket : WebSocket) (buffer : ReadOnlyMemory<byte>)  =
    websocket.ReceiveAsync(buffer, ct)

  let asyncSend(ws: WebSocket) (buffer: ReadOnlyMemory<byte>) (messageType: WebSocketMessageType) (endOfMessage: bool) =
    fun ct -> ws.SendAsync(buffer, messageType, endOfMessage, ct)
    |> Async.AwaitTaskWithCancellation
