module Helpers

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.IO
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.CommandLine.Binding
open System.CommandLine.Builder
open System.CommandLine.Parsing

open NBitcoin.Altcoins
open NBitcoin.DataEncoders
open NBitcoin
open NBitcoin.Crypto

open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost

open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.Projections
open NLoop.Server.Services


let getLocalBoltzClient() =
  let httpClient =new  HttpClient()
  httpClient.BaseAddress <- Uri("http://localhost:9001")
  let b = BoltzClient(httpClient)
  b

let private GetCertFingerPrint(filePath: string) =
  use cert = new X509Certificate2(filePath)
  use hashAlg = SHA256.Create()
  hashAlg.ComputeHash(cert.RawData)

let hex = HexEncoder()
let getCertFingerPrintHex (filePath: string) =
  GetCertFingerPrint filePath |> hex.EncodeData
let private checkConnection(port) =
  let l = TcpListener(IPAddress.Loopback, port)
  try
    l.Start()
    l.Stop()
    Ok()
  with
  | :? SocketException -> Error("")

let findEmptyPortUInt(ports: uint []) =
  let mutable i = 0
  while i < ports.Length do
    let mutable port = RandomUtils.GetUInt32() % 4000u
    port <- port + 10000u
    if (ports |> Seq.exists((=)port)) then () else
    match checkConnection((int)port) with
    | Ok _ ->
      ports.[i] <- port
      i <- i + 1
    | _ -> ()
  ports

let findEmptyPort(ports: int[]) =
  findEmptyPortUInt(ports |> Array.map(uint))

let getDummyLightningClientProvider() =
  { new ILightningClientProvider with
      member this.TryGetClient(cryptoCode) =
        failwith ""
      member this.GetAllClients() =
        failwith ""
      }

let mockCheckpointDB = {
  new ICheckpointDB
    with
    member this.GetSwapStateCheckpoint(ct: CancellationToken): ValueTask<int64 voption> =
      ValueTask.FromResult(ValueNone)
    member this.SetSwapStateCheckpoint(checkpoint: int64, ct:CancellationToken): ValueTask =
      ValueTask()
}

type TestStartup(env) =
  member this.Configure(appBuilder) =
    App.configureApp(appBuilder)

  member this.ConfigureServices(services) =
    App.configureServices true env services

let getTestHost() =
  WebHostBuilder()
    .UseContentRoot(Directory.GetCurrentDirectory())
    .ConfigureAppConfiguration(fun configBuilder ->
      configBuilder.AddJsonFile("appsettings.test.json") |> ignore
      )
    .UseStartup<TestStartup>()
    .ConfigureLogging(Main.configureLogging)
    .ConfigureTestServices(fun (services: IServiceCollection) ->
      let rc = NLoopServerCommandLine.getRootCommand()
      let p =
        CommandLineBuilder(rc)
          .UseMiddleware(Main.useWebHostMiddleware)
          .Build()
      services
        .AddHttpClient<BoltzClient>()
        .ConfigureHttpClient(fun _sp _client ->
          () // TODO: Inject Mock ?
          )
        |> ignore
      services
        .AddSingleton<BindingContext>(BindingContext(p.Parse(""))) // dummy for NLoop to not throw exception in `BindCommandLine`
        .AddSingleton<ILightningClientProvider>(getDummyLightningClientProvider())
        .AddSingleton<ICheckpointDB>(mockCheckpointDB)
        |> ignore
    )
    .UseTestServer()

