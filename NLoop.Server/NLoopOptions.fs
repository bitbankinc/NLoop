namespace NLoop.Server

open System
open System.Collections.Generic
open System.IO
open NBitcoin
open NBitcoin.Altcoins
open NBitcoin.RPC
open NLoop.Domain

type ChainOptions() =
  static member val Instance = ChainOptions() with get
  member val RPCHost = "localhost" with get, set
  member val RPCPort = 18332 with get, set
  member val RPCUser = String.Empty with get, set
  member val RPCPassword = String.Empty with get, set
  member val RPCCookieFile = String.Empty with get, set

  member val LightningConnectionString = String.Empty with get, set

  member val CryptoCode = SupportedCryptoCode.BTC with get, set

  member this.GetNetwork(chainName: string) =
    this.CryptoCode.ToNetworkSet().GetNetwork(ChainName chainName)

  member this.GetRPCClient(chainName: string) =
    RPCClient($"{this.RPCUser}:{this.RPCPassword}", $"{this.RPCHost}:{this.RPCPort}", this.GetNetwork(chainName))

type NLoopOptions() =
  // -- general --
  static member val Instance = NLoopOptions() with get
  member val ChainOptions = Dictionary<SupportedCryptoCode, ChainOptions>() with get
  member val Network = Network.Main.ChainName.ToString() with get, set

  member this.GetNetwork(cryptoCode: SupportedCryptoCode) =
    this.ChainOptions.[cryptoCode].GetNetwork(this.Network)

  member this.GetRPCClient(cryptoCode: SupportedCryptoCode) =
    this.ChainOptions.[cryptoCode].GetRPCClient(this.Network)

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

  // -- boltz --
  member val BoltzHost = Constants.DefaultBoltzServer with get, set
  member val BoltzPort = Constants.DefaultBoltzPort with get, set
  member val BoltzHttps = Constants.DefaultBoltzHttps with get, set
  // -- --

  // -- eventstore db --
  member val EventStoreUrl =
      let protocol = "tcp"
      let host = "localhost"
      let port = 2113
      let user = "admin"
      let password = "changeit"
      $"%s{protocol}://%s{user}:%s{password}@%s{host}:%i{port}"
      with get, set

  // -- --

  member val MaxAcceptableSwapFeeSat = 10000L with get, set
  member this.MaxAcceptableSwapFee = Money.Satoshis(this.MaxAcceptableSwapFeeSat)

  member val MinimumSwapAmountSatoshis = 1000L with get, set

  member val AcceptZeroConf = false with get, set

  member val OnChainCrypto = [|SupportedCryptoCode.BTC|] with get, set
  member val OffChainCrypto = [|SupportedCryptoCode.BTC|] with get, set

  member this.OnChainNetworks = this.OnChainCrypto |> Array.map(fun s -> s.ToString().GetNetworkSetFromCryptoCodeUnsafe())
  member this.OffChainNetworks = this.OffChainCrypto |> Array.map(fun s -> s.ToString().GetNetworkSetFromCryptoCodeUnsafe())

  member this.DBPath = Path.Join(this.DataDir, "nloop.db")
