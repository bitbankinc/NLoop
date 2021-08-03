namespace NLoop.Server.Services

open System.Threading
open Microsoft.Extensions.Hosting
open FSharp.Control.Tasks

type AutoLoopManager() =
  inherit BackgroundService()

  override this.ExecuteAsync(ct: CancellationToken) = unitTask {
    return ()
  }


