/// Port from: https://github.com/nreco/logging
namespace NLoop.Server

open System
open System.Linq
open System.Collections.Concurrent
open System.IO
open System.Runtime.CompilerServices
open System.Text
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open FSharp.Control.Tasks

[<Struct>]
type LogMessage = {
  LogName: string
  Message: string
  LogLevel: LogLevel
  EventId: EventId
  Exception: Exception
}

type FileError(logFileName, ex: Exception) =
  member val LogFileName = logFileName with get, set
  member val ErrorException = ex with get, set
  member val internal NewLogFileName = null with get, set
  member this.UseNewLogFileName(newLogFileName: string) =
    this.NewLogFileName <- newLogFileName

type FileLoggerOptions() =
  /// Append to existing log files or override them.
  member val Append = true with get, set

  member val FileSizeLimitBytes = 0 with get, set
  member val MaxRollingFiles = 0 with get, set

  member val FormatLogEntry: Func<LogMessage, string> =
    Func<_, _>(fun s -> s.Message) with get, set

  member val MinLevel = LogLevel.Trace with get, set
  member val FormatLogFileName = Func<string, string> id with get, set
  member val HandleFileError = Action<FileError>(fun _ -> ()) with get, set

type FileLoggerConfig() =
  member val Path: string = null with get, set
  member val Append = true with get, set
  member val FileSizeLimitBytes = 0 with get, set
  member val MaxRollingFile = 0 with get, set
  member val MinLevel = LogLevel.Trace with get, set


type FileLoggerProvider(fileName: string, options: FileLoggerOptions) as this =
  let mutable logFileName = fileName
  let loggers = ConcurrentDictionary<string, FileLogger>()
  let entryQueue = Channel.CreateBounded<string>(1024)
  let mutable fWriter: FileWriter = null
  let mutable processQueueTask: Task = null
  let processQueue () = task {
    let mutable finished = false
    while not <| finished do
      let! f = entryQueue.Reader.WaitToReadAsync()
      finished <- f
      if not <| finished then
        let! msg = entryQueue.Reader.ReadAsync()
        this.FWriter.WriteMessage(msg, entryQueue.Reader.CanCount && entryQueue.Reader.Count = 0)
  }
  do
    logFileName <- Environment.ExpandEnvironmentVariables fileName
    processQueueTask <- Task.Factory.StartNew(processQueue, TaskCreationOptions.LongRunning)
    ()
  member val Append = options.Append with get
  member val LogFileName = logFileName with get, set
  member val FileSizeLimitBytes = options.FileSizeLimitBytes with get
  member val MaxRollingFiles = options.MaxRollingFiles with get
  member private this.FWriter
    with get(): FileWriter =
      if fWriter |> isNull then
        fWriter <- FileWriter(this)
      fWriter
    and set v =
      fWriter <- v

  member val FormatLogEntry: Func<LogMessage, string> =
    options.FormatLogEntry with get, set

  member val MinLevel = options.MinLevel with get, set
  member val FormatLogFileName: Func<string, string> = options.FormatLogFileName with get, set
  member val HandleFileError = options.HandleFileError with get, set

  new (fileName: string, append: bool) =
    let o = FileLoggerOptions()
    o.Append <- append
    new FileLoggerProvider(fileName, o)
  new (fileName: string) = new FileLoggerProvider(fileName, true)

  member this.CreateLogger(categoryName: string)=
    loggers.GetOrAdd(categoryName, fun name -> FileLogger(name, this))
    :> ILogger

  member internal this.WriteEntry(msg: string) =
    let t = entryQueue.Writer.WriteAsync msg
    if t.IsCompletedSuccessfully then
      ()
    else
      t.GetAwaiter().GetResult()

  interface ILoggerProvider with
    member this.Dispose() =
      entryQueue.Writer.Complete()
      loggers.Clear()
      this.FWriter.Close()

    member this.CreateLogger categoryName = this.CreateLogger categoryName

and [<AllowNullLiteral>] internal FileWriter(fileLogPrev: FileLoggerProvider) as this =
  let mutable __lastBaseLogFileName = null
  let mutable logFileName = null
  let mutable logFileStream: Stream = null
  let mutable logFileWriter: TextWriter = null
  do
    this.DetermineLastFileLogName()
    this.OpenFile(fileLogPrev.Append)
  member private this.GetBaseLogFileName() =
     fileLogPrev.FormatLogFileName.Invoke fileLogPrev.LogFileName

  member private this.DetermineLastFileLogName() =
    let baseLogFileName = this.GetBaseLogFileName()
    __lastBaseLogFileName <- baseLogFileName
    if fileLogPrev.FileSizeLimitBytes > 0 then
      let logFileMask = Path.GetFileNameWithoutExtension baseLogFileName + "*" + Path.GetExtension baseLogFileName
      let logDirName =
        let d = (Path.GetDirectoryName baseLogFileName)
        if String.IsNullOrEmpty d then
          Directory.GetCurrentDirectory()
        else
          d
      let logFiles =
        if Directory.Exists logDirName then
          Directory.GetFiles(logDirName, logFileMask, SearchOption.TopDirectoryOnly)
        else [||]
      if logFiles.Length > 0 then
        let lastFileInfo =
          logFiles
            .Select(FileInfo)
            .OrderByDescending(fun fInfo -> fInfo.Name)
            .OrderByDescending(fun fInfo -> fInfo.LastWriteTime)
            .First()
        logFileName <- lastFileInfo.FullName
      else
        logFileName <- baseLogFileName
    else
      logFileName <- baseLogFileName
  member private this.OpenFile(append: bool) =
    let createLogFileStream () =
      let fInfo = FileInfo(logFileName)
      fInfo.Directory.Create()
      logFileStream <- new FileStream(logFileName, FileMode.OpenOrCreate,FileAccess.Write)
      if append then
        logFileStream.Seek(0L, SeekOrigin.End) |> ignore
      else
        logFileStream.SetLength(0L) // clear the file
    try
      createLogFileStream()
    with
    | ex ->
      let fileErr = FileError(logFileName, ex)
      fileLogPrev.HandleFileError.Invoke fileErr
      if fileErr.NewLogFileName |> isNull |> not then
        fileLogPrev.LogFileName <- fileErr.NewLogFileName
        this.DetermineLastFileLogName()
        createLogFileStream()
      else
        reraise()

  member private this.GetNextFileLogName() =
    let baseLogFileName = this.GetBaseLogFileName()
    // if file does not exist or file size limit is not reached -- do not add rolling file index.
    if not <| File.Exists(baseLogFileName) || fileLogPrev.FileSizeLimitBytes <= 0 ||
       FileInfo(baseLogFileName).Length < (fileLogPrev.FileSizeLimitBytes |> int64) then
      baseLogFileName
    else
      let mutable currentFileIndex = 0
      let baseFileNameOnly = Path.GetFileNameWithoutExtension baseLogFileName
      let currentFileNameOnly = Path.GetFileNameWithoutExtension logFileName

      let suffix  = currentFileNameOnly.Substring(baseFileNameOnly.Length)
      let mutable parsedIndex = 0
      if suffix.Length > 0 && Int32.TryParse(suffix, &parsedIndex) then
        currentFileIndex <- parsedIndex

      let mutable nextFileIndex = currentFileIndex + 1
      if fileLogPrev.MaxRollingFiles > 0 then
        nextFileIndex <- nextFileIndex % fileLogPrev.MaxRollingFiles

      let nextFileName =
        baseFileNameOnly +
        (if nextFileIndex > 0 then nextFileIndex.ToString() else "") +
        (Path.GetExtension(baseLogFileName))
      Path.Combine(Path.GetDirectoryName(baseLogFileName), nextFileName)
  member private this.CheckForNewLogFile() =
    let isMaxFileSizeThresholdReached =
      fileLogPrev.FileSizeLimitBytes > 0 && logFileStream.Length > (int64 fileLogPrev.FileSizeLimitBytes)
    let isBaseFileNameChanged =
      if fileLogPrev.FormatLogFileName |> isNull |> not then
        let baseLogFileName = this.GetBaseLogFileName()
        if baseLogFileName <> __lastBaseLogFileName then
           __lastBaseLogFileName <- baseLogFileName
           true
        else
          false
      else
        false

    let openNewFile = isMaxFileSizeThresholdReached || isBaseFileNameChanged
    if openNewFile then
      this.Close()
      logFileName <- this.GetNextFileLogName()
      this.OpenFile(false)
  member internal this.WriteMessage(msg: string, flush: bool) =
    if logFileWriter |> isNull |> not then
      this.CheckForNewLogFile()
      logFileWriter.WriteLine(msg)
      if flush then
        logFileWriter.Flush()

  member internal this.Close() =
    if logFileWriter |> isNull |> not then
      logFileWriter.Dispose()
      logFileWriter <- null
      logFileStream.Dispose()
      logFileStream <- null

and FileLogger(logName: string, loggerPrv: FileLoggerProvider) =
  let getShortLogLevel (level: LogLevel) =
    match level with
    | LogLevel.Trace -> "TRCE"
    | LogLevel.Debug -> "DBUG"
    | LogLevel.Information -> "INFO"
    | LogLevel.Warning -> "WARN"
    | LogLevel.Error -> "FAIL"
    | LogLevel.Critical -> "CRIT"
    | x -> x.ToString().ToUpperInvariant()

  member this.IsEnabled(logLevel) =
      logLevel >= loggerPrv.MinLevel

  interface ILogger with
    member this.BeginScope<'TState>(_state: 'TState) =
      null
    member this.IsEnabled (logLevel: LogLevel) = this.IsEnabled(logLevel)

    member this.Log<'TState>(level: LogLevel, eventId: EventId, state: 'TState, exp: Exception, formatter: Func<'TState, Exception, string>)=
      if not <| this.IsEnabled level then () else
      if formatter |> isNull then raise <| ArgumentNullException (nameof(formatter)) else
      let msg = formatter.Invoke(state, exp)
      if loggerPrv.FormatLogEntry |> isNull |> not then
        {
          LogMessage.LogName = logName
          LogLevel = level
          EventId = eventId
          Message = msg
          Exception = exp
        }
        |> loggerPrv.FormatLogEntry.Invoke
        |> loggerPrv.WriteEntry
      else
        let logBuilder = StringBuilder()
        if not <| String.IsNullOrEmpty msg then
          logBuilder
            .Append(DateTime.Now.ToString("o"))
            .Append('\t')
            .Append(getShortLogLevel(level))
            .Append("\t[")
            .Append(logName)
            .Append(']')
            .Append("\t[")
            .Append(eventId)
            .Append("]\t")
            .Append(msg)
            |> ignore

        if exp |> isNull |> not then
            logBuilder.AppendLine(exp.ToString()) |> ignore

        loggerPrv.WriteEntry(logBuilder.ToString())


[<AbstractClass;Sealed;Extension>]
type FileLoggerExtensions =
  [<Extension>]
  static member AddFile(this: ILoggingBuilder, filename: string, ?append: bool) =
    let append = defaultArg append true
    this
      .Services
      .AddSingleton<ILoggerProvider, FileLoggerProvider>(fun _sp -> new FileLoggerProvider(filename, append))
      |> ignore
    this

  [<Extension>]
  static member AddFile(this: ILoggingBuilder, fileName: string, configure: Action<FileLoggerOptions>) =
    this.Services.AddSingleton<ILoggerProvider, FileLoggerProvider>(fun _sp ->
      let opts = FileLoggerOptions()
      configure.Invoke opts
      new FileLoggerProvider(fileName, opts)
    )
    |> ignore
    this

  static member private CreateFromConfiguration(configuration: IConfiguration, configure: Action<FileLoggerOptions>): FileLoggerProvider voption =
    let fileSection = configuration.GetSection("File")
    if fileSection |> isNull then ValueNone else
    let config = FileLoggerConfig()
    fileSection.Bind(config)
    if (String.IsNullOrWhiteSpace(config.Path)) then ValueNone else
    let fileLoggerOptions = FileLoggerOptions()
    fileLoggerOptions.Append <- config.Append
    fileLoggerOptions.MinLevel <- config.MinLevel
    fileLoggerOptions.FileSizeLimitBytes <- config.FileSizeLimitBytes
    fileLoggerOptions.MaxRollingFiles <- config.MaxRollingFile
    if configure |> isNull |> not then
      configure.Invoke fileLoggerOptions
    ValueSome (new FileLoggerProvider(config.Path, fileLoggerOptions))

  [<Extension>]
  static member AddFile(this: ILoggingBuilder, configuration: IConfiguration, ?configure: Action<FileLoggerOptions>) =
    let configure = defaultArg configure (Action<_>(fun _ -> ()))
    let fileLoggerPrv = FileLoggerExtensions.CreateFromConfiguration(configuration, configure)
    fileLoggerPrv |> ValueOption.iter(fun provider ->
      this.Services.AddSingleton<ILoggerProvider, FileLoggerProvider>(fun _sp ->
        provider
      )
      |> ignore
    )
    this

