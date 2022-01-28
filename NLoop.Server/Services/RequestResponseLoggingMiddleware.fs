namespace NLoop.Server.Services

open System
open System.IO
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Microsoft.IO
open FSharp.Control.Tasks.Affine


type RequestResponseLoggingMiddleware(logger: ILogger<RequestResponseLoggingMiddleware>, memStreamManager: RecyclableMemoryStreamManager) =
  member private this.ReadStreamInChunks(stream: Stream) =
    let readChunkBufferLength = 4096
    stream.Seek(0L, SeekOrigin.Begin) |> ignore
    use textWriter = new StringWriter()
    use reader = new StreamReader(stream)
    let readChunk = Array.zeroCreate<char> readChunkBufferLength
    let mutable readChunkLength = reader.ReadBlock(readChunk, 0, readChunkBufferLength)
    while (readChunkLength > 0) do
      readChunkLength <- reader.ReadBlock(readChunk, 0, readChunkBufferLength)
      textWriter.Write(readChunk, 0, readChunkLength)
      ()
    textWriter.ToString()
  interface IMiddleware with
    member this.InvokeAsync(context, next) = unitTask {
      context.Request.EnableBuffering()
      use requestStream = memStreamManager.GetStream()
      do! context.Request.Body.CopyToAsync(requestStream)
      logger.LogTrace($"Http Request Information: ")
      let msg =
        $"Received Request{Environment.NewLine}" +
        $"Method: {context.Request.Method} " +
        $"Scheme: {context.Request.Scheme} " +
        $"Host: {context.Request.Host} " +
        $"Path: {context.Request.Path} " +
        $"QueryString: {context.Request.QueryString} " +
        $"Request Body: {this.ReadStreamInChunks(requestStream)}"

      logger.LogTrace(msg)
      context.Request.Body.Position <- 0L;
      let originalBodyStream = context.Response.Body;
      use responseBody = memStreamManager.GetStream();
      context.Response.Body <- responseBody;
      let! _ = next.Invoke(context);

      context.Response.Body.Seek(0L, SeekOrigin.Begin) |> ignore;
      let! text = (new StreamReader(context.Response.Body)).ReadToEndAsync();
      context.Response.Body.Seek(0L, SeekOrigin.Begin) |> ignore;

      logger.LogTrace($"Http Response Information :{Environment.NewLine}" +
                      $"Scheme: {context.Request.Scheme} " +
                      $"Host: {context.Request.Host} " +
                      $"Path: {context.Request.Path} " +
                      $"QueryString: {context.Request.QueryString} " +
                      $"Response Body: {text}");
      do! responseBody.CopyToAsync(originalBodyStream);
    }
