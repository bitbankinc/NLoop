namespace LndClient

open System
open System.Net.WebSockets
open System.Threading
open FSharp.Control.Tasks

type WebsocketNotificationSession<'TEvent>(client: LndNSwagClient) =
  inherit NotificationSessionBase<'TEvent>()

  let mutable messageListener = null

  interface IDisposable with
    member this.Dispose() =
      ()


  member private this.ConnectAsyncCore(uri, ct) =
    ()
  member internal this.ListenChannelChangeAsync(ct: CancellationToken) =
    let uri =
      client.GetFullUri("/v1/channels/subscribe")
      |> toWebSocketUri
    task {
      let! socket = this.ConnectAsyncCore(uri, ct)
      messageListener <- WebSocketMessageListener(socket, settings)
    }

  override this.NextEventAsync(ct) = failwith "todo"
