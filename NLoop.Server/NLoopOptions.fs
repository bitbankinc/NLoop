namespace NLoop.Server

open System
open System.IO
open NBitcoin

type NLoopOptions() =
  // -- general --
  static member val Instance = NLoopOptions() with get
  member val Network = Network.Main with get, set
  member val DataDir = Constants.DefaultDataDirectoryPath with get, set
  // -- --
  // -- https --
  member val NoHttps = Constants.DefaultNoHttps with get, set
  member val HttpsPort: int = Constants.DefaultHttpsPort with get, set
  member val HttpsCert = Constants.DefaultHttpsCertFile with get, set
  member val HttpsCertPass = String.Empty with get, set
  // -- --
  // -- rpc --
  member val RPCPort = Constants.DefaultRPCPort with get, set
  member val RPCHost = Constants.DefaultRPCHost with get, set
  member val RPCCookieFile = Constants.DefaultCookieFile with get,set
  member val RPCAllowIP = Constants.DefaultRPCAllowIp with get, set
  member val NoAuth = false with get, set
  // -- --

  member val MaxAcceptableSwapFeeSat = 10000L with get, set
  member this.MaxAcceptableSwapFee = Money.Satoshis(this.MaxAcceptableSwapFeeSat)

  member this.DBPath = Path.Join(this.DataDir, "nloop.db")
