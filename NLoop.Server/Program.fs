namespace NLoop.Server

open System
open System.IO
open System.Text.Json
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Authentication.Certificate
open Giraffe

open Microsoft.IO
open NLoop.Server
open NLoop.Server.DTOs
open NLoop.Server.LoopHandlers
open NLoop.Server.Services

open Microsoft.Extensions.Hosting

module App =
  let noCookie =
    RequestErrors.UNAUTHORIZED
      "Basic"
      "Access to the protected API"
      "You must authenticate with cookie or certificate"

  let mustAuthenticate = requiresAuthentication noCookie

  let webApp =
    choose [
      subRoutef "/v1/%s" (fun cryptoCode ->
        choose [
          POST >=>
            route "/loop/out" >=> mustAuthenticate >=> bindJson<LoopOutRequest> (handleLoopOut cryptoCode)
            route "/loop/in" >=> mustAuthenticate >=> bindJson<LoopInRequest> (handleLoopIn cryptoCode)
      ])
      subRoute "/v1" (choose [
        GET >=>
          route "/info" >=> handleGetInfo
          route "/version" >=> json Constants.AssemblyVersion
        ])
      setStatusCode 404 >=> text "Not Found"
    ]

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
          app
            .UseDeveloperExceptionPage()
            .UseMiddleware<RequestResponseLoggingMiddleware>()
      | false ->
          app
            .UseGiraffeErrorHandler(errorHandler)
            .UseHttpsRedirection())
          .UseCors(configureCors)
          .UseAuthentication()
          .UseGiraffe(webApp)

  let configureServices (conf: IConfiguration) (env: IHostEnvironment) (services : IServiceCollection) =
      let n = conf.GetChainName()

      // json settings
      let jsonOptions = JsonSerializerOptions()
      jsonOptions.AddNLoopJsonConverters(n)
      services
        .AddSingleton(jsonOptions)
        .AddSingleton<Json.ISerializer>(SystemTextJson.Serializer(jsonOptions)) |> ignore // for giraffe

      services.AddNLoopServices(conf) |> ignore

      if (env.IsDevelopment()) then
        services.AddTransient<RequestResponseLoggingMiddleware>() |> ignore
        services.AddSingleton<RecyclableMemoryStreamManager>() |> ignore
      else
        ()

      services.AddCors()    |> ignore

      services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCertificate(fun o -> o.AllowedCertificateTypes <- CertificateTypes.SelfSigned)
        .AddCertificateCache()
        .AddCookie(fun _o -> ())
        |> ignore

      services.AddGiraffe() |> ignore


type Startup(conf: IConfiguration, env: IHostEnvironment) =
  member this.Configure(appBuilder) =
    App.configureApp(appBuilder)

  member this.ConfigureServices(services) =
    App.configureServices conf env services

module Main =

  let configureLogging (builder : ILoggingBuilder) =
      builder
        .AddConsole()
        .AddDebug()
#if DEBUG
        .SetMinimumLevel(LogLevel.Debug)
#else
        .SetMinimumLevel(LogLevel.Information)
#endif
        |> ignore

  let configureConfig args (builder: IConfigurationBuilder) =
    builder.SetBasePath(Directory.GetCurrentDirectory()) |> ignore
    Directory.CreateDirectory(Constants.HomeDirectoryPath) |> ignore
    let iniFile = Path.Join(Constants.HomeDirectoryPath, "nloop.conf")
    if (iniFile |> File.Exists) then
      builder.AddIniFile(iniFile) |> ignore
    builder.AddEnvironmentVariables(prefix="NLOOP_") |> ignore
    builder.AddCommandLine(args=args) |> ignore
    ()

  [<EntryPoint>]
  let main args =
      Host.CreateDefaultBuilder(args)
          .ConfigureAppConfiguration(configureConfig args)
          .ConfigureWebHostDefaults(
              fun webHostBuilder ->
                  webHostBuilder
                      .UseStartup<Startup>()
                      // .ConfigureKestrel(fun o ->
                        //o.ConfigureHttpsDefaults(fun o -> o.ClientCertificateMode <- ClientCertificateMode.RequireCertificate)
                      //)
                      .ConfigureLogging(configureLogging)
                      |> ignore)
          .Build()
          .Run()
      0
