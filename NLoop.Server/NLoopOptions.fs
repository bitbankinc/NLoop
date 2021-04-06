namespace NLoop.Server

open System
open System.Collections.Generic
open System.IO
open NBitcoin
open NBitcoin.Altcoins

type ChainOptions() =
  static member val Instance = ChainOptions() with get
  member val RPCHost = "localhost" with get, set
  member val RPCPort = 18332 with get, set
  member val RPCUser = String.Empty with get, set
  member val RPCPassword = String.Empty with get, set
  member val RPCCookieFile = String.Empty with get, set

  member val CryptoCode = SupportedCryptoCode.BTC with get, set

  member this.GetNetwork(chainName: string) =
    this.CryptoCode.ToNetworkSet().GetNetwork(ChainName chainName)

type NLoopOptions() =
  // -- general --
  static member val Instance = NLoopOptions() with get
  member val ChainOptions = Dictionary<SupportedCryptoCode, ChainOptions>() with get
  member val Network = Network.Main.ChainName.ToString() with get, set

  member this.GetNetwork(cryptoCode: SupportedCryptoCode) =
    this.ChainOptions.[cryptoCode].GetNetwork(this.Network)

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
  member val RPCCors = [|"https://localhost:5001"; "http://localhost:5000"|] with get,set
  // -- --

  member val MaxAcceptableSwapFeeSat = 10000L with get, set
  member this.MaxAcceptableSwapFee = Money.Satoshis(this.MaxAcceptableSwapFeeSat)

  member val AcceptZeroConf = false with get, set

  member val OnChainCrypto = [|SupportedCryptoCode.BTC|] with get, set
  member val OffChainCrypto = [|SupportedCryptoCode.BTC|] with get, set

  member this.OnChainNetworks = this.OnChainCrypto |> Array.map(fun s -> s.ToString().GetNetworkSetFromCryptoCodeUnsafe())
  member this.OffChainNetworks = this.OffChainCrypto |> Array.map(fun s -> s.ToString().GetNetworkSetFromCryptoCodeUnsafe())

  member this.DBPath = Path.Join(this.DataDir, "nloop.db")
