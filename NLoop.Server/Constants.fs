namespace NLoop.Server

open System
open System.IO

[<RequireQualifiedAccess>]
module Constants =
  let HomePath =
     if (Environment.OSVersion.Platform = PlatformID.Unix || Environment.OSVersion.Platform = PlatformID.MacOSX)
     then Environment.GetEnvironmentVariable("HOME")
     else Environment.GetEnvironmentVariable("%HOMEDRIVE%%HOMEPATH%")
     |> fun h -> if (isNull h) then raise <| Exception("Failed to define home directory path") else h

  [<Literal>]
  let HomeDirectoryName = ".nloop"
  let HomeDirectoryPath = Path.Join(HomePath, HomeDirectoryName)
  let DefaultDataDirectoryPath = Path.Join(HomeDirectoryPath, "data")

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
  let DefaultRPCPort = "localhost"

  let DefaultRPCAllowIp = [|"localhost"|]

  [<Literal>]
  let DefaultBoltzServer = "https://boltz.exchange/api"

  [<Literal>]
  let DefaultBoltzPort = 443

  type private Foo = Bar
  let AssemblyVersion =
    Bar.GetType().Assembly.GetName().Version.ToString()
