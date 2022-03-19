namespace NLoop.Server.Services

open System.Collections.Concurrent
open System.Net
open System.Threading.Tasks
open Microsoft.AspNetCore.Connections
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

type StreamJsonRpcHost(connectionListenerFactory: IConnectionListenerFactory, _logger: ILogger<StreamJsonRpcHost>) =
  inherit BackgroundService()

  let connections = ConcurrentDictionary<string, ConnectionContext * Task>()
  let mutable _connectionListener = None

  member private this.AcceptAsync(_conn: ConnectionContext) =
    task {
      try
        return ()
      with
      | :? ConnectionResetException
      | :? ConnectionAbortedException ->
        ()
      | e ->
        _logger.LogError(e, "")
    } :> Task
  override this.ExecuteAsync(stoppingToken) =
    task {
      let! connectionListener = connectionListenerFactory.BindAsync(IPEndPoint(IPAddress.Loopback, 6000), stoppingToken)
      _connectionListener <- Some connectionListener
      while not <| stoppingToken.IsCancellationRequested do
        let!  connectionContext = connectionListener.AcceptAsync(stoppingToken)
        if connectionContext |> isNull then () else
        connections.[connectionContext.ConnectionId] <- (connectionContext, this.AcceptAsync(connectionContext))

      let connectionsExecutionTask = ResizeArray(connections.Count)
      for conn in connections do
        let ctx, t = conn.Value
        connectionsExecutionTask.Add(t)
        ctx.Abort()
      do! Task.WhenAll(connectionsExecutionTask)
    } :> Task



  override this.StopAsync(_ct) =
    task {
      match _connectionListener with
      | Some c -> do! c.DisposeAsync()
      | _ -> ()
    } :> Task
