namespace NLoop.Server

open System
open System.Collections.Generic
open System.IO
open LndClient
open NBitcoin
open NBitcoin.Altcoins
open NBitcoin.RPC
open NLoop.Domain

type ChainOptions() =
  static member val Instance = ChainOptions() with get
  member val CryptoCode = SupportedCryptoCode.BTC with get, set

  // --- on-chain node host ---
  member val RPCHost = "localhost" with get, set
  member val RPCPort = 18332 with get, set
  member val RPCUser = String.Empty with get, set
  member val RPCPassword = String.Empty with get, set
  member val RPCCookieFile = String.Empty with get, set
  // --- ---

  // --- swap params ---

  /// Confirmation target for the sweep in on-chain swap
  member val SweepConf = 6 with get, set


  // --- ---


  // --- properties and methods ---

  member this.GetNetwork(chainName: string) =
    this.CryptoCode.ToNetworkSet().GetNetwork(ChainName chainName)

  member this.GetRPCClient(chainName: string) =
    RPCClient($"{this.RPCUser}:{this.RPCPassword}", $"{this.RPCHost}:{this.RPCPort}", this.GetNetwork(chainName))


type NLoopOptions() =
  // -- general --
  static member val Instance = NLoopOptions() with get
  member val ChainOptions = Dictionary<SupportedCryptoCode, ChainOptions>() with get
  member val Network = Network.Main.ChainName.ToString() with get, set
  member this.ChainName =
    this.Network |> ChainName

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

  // -- swap fee limitations --
  member val MaxAcceptableSwapFeeSats = 10000L with get, set
  member this.MaxAcceptableSwapFee = Money.Satoshis(this.MaxAcceptableSwapFeeSats)
  member val MinimumSwapAmountSats = 1000L with get, set
  member val MaxPrepaySats = 1000L with get, set
  member this.MaxPrepay = Money.Satoshis(this.MaxPrepaySats)
  // -- --

  // -- autoloop --
  /// The maximum number of the swaps simultaneously dispatched by an autolooper.
  member val MaxAutoInFlight = 1 with get, set
  // -- --

  member val AcceptZeroConf = false with get, set

  member val OnChainCrypto = [|SupportedCryptoCode.BTC|] with get, set
  member val OffChainCrypto = [|SupportedCryptoCode.BTC|] with get, set

  member this.OnChainNetworks =
    this.OnChainCrypto
    |> Array.map(fun s -> s.ToString().GetNetworkSetFromCryptoCodeUnsafe())
  member this.OffChainNetworks =
    this.OffChainCrypto
    |> Array.map(fun s -> s.ToString().GetNetworkSetFromCryptoCodeUnsafe())

  member this.DBPath = Path.Join(this.DataDir, "nloop.db")

  member val LndCert = null with get, set
  member val LndMacaroon = null with get, set
  member val LndMacaroonFilePath = null with get, set
  member val LndGrpcServer = "https://localhost:10009" with get, set

  member val LndAllowUnsafe = false with get, set

  member this.GetLndGrpcSettings() =
    LndGrpcSettings.Create(
      this.LndGrpcServer,
      this.LndCert |> Option.ofObj,
      this.LndMacaroon |> Option.ofObj,
      this.LndMacaroonFilePath |> Option.ofObj
    )
    |>
      function
        | Ok x -> x
        | Error e -> failwith $"Invalid Lnd config: {e}"


