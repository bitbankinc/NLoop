namespace NLoop.Server

open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Giraffe
open NLoop.Infrastructure
open NLoop.Infrastructure.DTOs
open NLoop.Server.LoopHandlers

open FSharp.Control.Tasks.NonAffine
open Newtonsoft.Json

module App =
  let noCookie =
    RequestErrors.UNAUTHORIZED
      "Basic"
      "Access to the protected API"
      "You must authenticate with cookie"

  let mustHaveCookies = requiresAuthentication noCookie

  let webApp =
      choose [
          subRoute "/v1"
            (choose [
              POST >=>
                route "/loop/out" >=> mustHaveCookies >=> bindJson<LoopOutRequest> handleLoopOut
                route "/loop/in" >=> mustHaveCookies >=> bindJson<LoopInRequest> handleLoopIn
          ])
          setStatusCode 404 >=> text "Not Found" ]

  // ---------------------------------
  // Error handler
  // ---------------------------------

  let errorHandler (ex : Exception) (logger : ILogger) =
      logger.LogError(ex, "An unhandled exception has occurred while executing the request.")
      clearResponse >=> setStatusCode 500 >=> text ex.Message

  // ---------------------------------
  // Config and Main
  // ---------------------------------

  let configureCors (builder : CorsPolicyBuilder) =
      builder
          .WithOrigins(
              "http://localhost:5000",
              "https://localhost:5001")
         .AllowAnyMethod()
         .AllowAnyHeader()
         |> ignore

  let configureApp (app : IApplicationBuilder) =
      let env = app.ApplicationServices.GetService<IWebHostEnvironment>()
      (match env.IsDevelopment() with
      | true  ->
          app.UseDeveloperExceptionPage()
      | false ->
          app .UseGiraffeErrorHandler(errorHandler)
              .UseHttpsRedirection())
          .UseCors(configureCors)
          .UseGiraffe(webApp)

  let configureServices (conf: IConfiguration) (services : IServiceCollection) =
      let n = conf.GetNetworkType()
      let jsonOptions = JsonSerializerOptions()
      jsonOptions.AddNLoopJsonConverters(n)
      jsonOptions.Converters.Add(JsonFSharpConverter())
      services.AddSingleton(jsonOptions) |> ignore
      services.AddCors()    |> ignore
      services.AddGiraffe() |> ignore

  let configureLogging (builder : ILoggingBuilder) =
      builder.AddConsole()
             .AddDebug() |> ignore

  let configureConfig args (builder: IConfigurationBuilder) =
    builder.SetBasePath(Directory.GetCurrentDirectory()) |> ignore
    Directory.CreateDirectory(Constants.HomeDirectoryPath) |> ignore
    let iniFile = Path.Join(Constants.HomeDirectoryPath, "nloop.conf")
    if (iniFile |> File.Exists) then
      builder.AddIniFile(iniFile) |> ignore
    builder.AddEnvironmentVariables(prefix="NLOOP_") |> ignore
    builder.AddCommandLine(args=args) |> ignore
    ()

type Startup(conf: IConfiguration) =
  member this.Configure =
    App.configureApp

  member this.ConfigureServices(services) =
    App.configureServices conf services

module Main =
  open App
  [<EntryPoint>]
  let main args =
      Host.CreateDefaultBuilder(args)
          .ConfigureAppConfiguration(configureConfig args)
          .ConfigureWebHostDefaults(
              fun webHostBuilder ->
                  webHostBuilder
                      .Configure(Action<IApplicationBuilder> configureApp)
                      .UseStartup<Startup>()
                      .ConfigureLogging(configureLogging)
                      |> ignore)
          .Build()
          .Run()
      0
