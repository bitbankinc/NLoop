// Learn more about F# at http://docs.microsoft.com/dotnet/fsharp

open System
open System.CommandLine
open System.CommandLine.Binding
open System.CommandLine.Builder
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Net.Http
open Microsoft.AspNetCore.Hosting.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open NLoop.CLI.SubCommands
open NLoop.Server
open NLoopClient
open System.CommandLine.Hosting

[<EntryPoint>]
let main argv =
    let rc = NLoop.CLI.NLoopCLICommandLine.getRootCommand
    rc.Handler <- CommandHandler.Create<IHost>((fun (host: IHost) ->
      ())
    )
    CommandLineBuilder(rc)
      .UseDefaults()
      .UseHost(fun (hostBuilder:IHostBuilder) ->
          let ctx = hostBuilder.Properties.[typeof<InvocationContext>] :?> InvocationContext
          hostBuilder
            .ConfigureHostConfiguration(fun configBuilder ->
              Main.configureConfig configBuilder
            )
            .ConfigureServices(fun h ->
              h.AddHttpClient<NLoopClient>() |> ignore
            )
            |> ignore
        )
      .Build()
      .Invoke(argv)
