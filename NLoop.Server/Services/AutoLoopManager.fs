namespace NLoop.Server.Services

open System
open System.Threading
open Microsoft.Extensions.Hosting
open FSharp.Control.Tasks
open Microsoft.Extensions.Logging

type AutoLoopManager(logger: ILogger<AutoLoopManager>)  =
  inherit BackgroundService()

  override this.ExecuteAsync(ct: CancellationToken) = unitTask {
    try
      while not <| ct.IsCancellationRequested do
        return ()
    with
    | :? OperationCanceledException ->
      ()
    | ex ->
      logger.LogError($"{ex}")
  }


