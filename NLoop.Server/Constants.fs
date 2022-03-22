namespace NLoop.Server

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open DotNetLightning.Utils.Primitives
open NLoop.Domain

[<RequireQualifiedAccess>]
module Constants =

  let AssemblyVersion =
    Assembly.GetExecutingAssembly().GetName().Version.ToString()

  // --- paths ---
  let HomePath =
     let envHome = if System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) then "HOMEPATH" else "HOME"
     Environment.GetEnvironmentVariable(envHome)

  [<Literal>]
  let AppName = "nloopd"

  [<Literal>]
  let HomeDirectoryName = ".nloop"
  let HomeDirectoryPath = Path.Combine(HomePath, HomeDirectoryName)
  let DefaultDataDirectoryPath = Path.Combine(HomeDirectoryPath, "data")
  // --- ---

  // --- logging ---

  let DefaultLoggingSettings:  seq<KeyValuePair<string, string>> =
#if DEBUG
    [
      ("Default", "Debug")
      ("System.Net.Http", "Warning")
      ("Microsoft", "Information")
      ("Grpc", "Debug")
      ("Giraffe.Middleware.GiraffeMiddleware", "Information")
    ]
#else
    [
      ("Default", "Debug")
      ("System.Net.Http", "Warning")
      ("Microsoft", "Warning")
      ("Grpc", "Debug")
      ("Giraffe.Middleware.GiraffeMiddleware", "Information")
    ]
#endif
    |> Seq.map(fun (a, b) -> $"Logging:LogLevel:{a}", b) |> dict :> _


  // --- ---

  // --- rpc&http options ---
  [<Literal>]
  let DefaultNoHttps = false
  [<Literal>]
  let DefaultHttpsPort = 443

  [<Literal>]
  let DefaultHttpsHost = "localhost"

  let DefaultHttpsCertFile = Path.Combine(HomePath, ".aspnet", "https", "ssl.cert")

  let DefaultCookieFile = Path.Combine(DefaultDataDirectoryPath, "cookie")

  [<Literal>]
  let DefaultRPCHost = "localhost"
  [<Literal>]
  let DefaultRPCPort = 5000

  let DefaultRPCAllowIp = [|"localhost"|]
  // --- ---

  // --- external services ---
  [<Literal>]
  let DefaultBoltzServer = "https://boltz.exchange/api"

  [<Literal>]
  let DefaultBoltzPort = 443


  [<Literal>]
  let DefaultLightningConnectionString = "type=lnd-rest;server=http://localhost:8080;allowinsecure=true"
  // --- ---

  [<Literal>]
  let FallbackFeeSatsPerByte = 50
  /// Minimum confirmation target user can specify.
  let [<Literal>] MinConfTarget = 2u

  /// The longest time we wait before giving up making an off-chain offer.
  let [<Literal>] OfferTimeoutSeconds = 15

  let [<Literal>] MaxRateDiffDelta: int64<ppm> = 100L<ppm>

  let [<Literal>] BlockchainLongPollingIntervalSec = 8.
  let [<Literal>] ExchangeLongPollingIntervalSec = 20.

  let MaxBlockRewind = BlockHeightOffset32 1200u


  let [<Literal>] ApiDocUrl = "https://bitbankinc.github.io/NLoop/"
