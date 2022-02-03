namespace NLoop.Server

open System
open System.Collections.Generic
open System.IO
open LndClient
open NBitcoin
open NLoop.Domain
open NLoop.Server.DTOs
open NLoop.Server.Options

type NLoopOptions() =
  // -- general --
  static member val Instance = NLoopOptions() with get
  member val ChainOptions = Dictionary<SupportedCryptoCode, IChainOptions>() with get
  member val Network = Network.Main.ChainName.ToString() with get, set
  member this.ChainName =
    this.Network |> ChainName

  member this.GetNetwork(cryptoCode: SupportedCryptoCode) =
    this.ChainOptions.[cryptoCode].GetNetwork(this.Network)

  member this.GetRPCClient(cryptoCode: SupportedCryptoCode) =
    this.ChainOptions.[cryptoCode].GetRPCClient(this.Network)

  member this.GetBlockChainClient cc =
    cc |> this.GetRPCClient |> RPCBlockchainClient :> IBlockChainClient

  member this.GetWalletClient cc =
    this.GetRPCClient cc
    |> BitcoindWalletClient
    :> IWalletClient

  member val DataDir = Constants.DefaultDataDirectoryPath with get, set
  member this.DataDirNetwork = Path.Combine(this.DataDir, this.Network)
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
  member this.BoltzUrl =
    let host = if this.BoltzHost.EndsWith "/" then this.BoltzHost else $"{this.BoltzHost}/"
    let u = UriBuilder(host)
    u.Port <- this.BoltzPort
    u.Uri
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

  member val OnChainCrypto = [|SupportedCryptoCode.BTC; SupportedCryptoCode.LTC|] with get, set
  member val OffChainCrypto = [|SupportedCryptoCode.BTC|] with get, set

  member this.GetPairIds(cat: Swap.Category) =
    match cat with
    | Swap.Category.Out ->
      seq [
        for baseAsset in this.OnChainCrypto do
          for quoteAsset in this.OffChainCrypto do
            PairId(baseAsset, quoteAsset)
      ]
    | Swap.Category.In ->
      seq [
        for baseAsset in this.OffChainCrypto do
          for quoteAsset in this.OnChainCrypto do
            PairId(baseAsset, quoteAsset)
      ]

  member this.PairIds =
    seq [this.GetPairIds Swap.Category.Out; this.GetPairIds Swap.Category.In]
    |> Seq.concat
    |> Seq.distinct

  member this.SwapGroups =
    let g cat =
      seq [
        for p in this.GetPairIds(cat) ->
          {
            Swap.Group.PairId = p
            Swap.Group.Category = cat
          }
      ]

    seq [
      yield! g Swap.Category.Out
      yield! g Swap.Category.In
    ]

  member this.OnChainNetworks =
    this.OnChainCrypto
    |> Array.map(fun s -> s.ToString().GetNetworkSetFromCryptoCodeUnsafe())
  member this.OffChainNetworks =
    this.OffChainCrypto
    |> Array.map(fun s -> s.ToString().GetNetworkSetFromCryptoCodeUnsafe())

  member val LndCertThumbPrint = null with get, set
  member val LndMacaroon = null with get, set
  member val LndMacaroonFilePath = null with get, set
  member val LndGrpcServer = "https://localhost:10009" with get, set
  member val LndAllowInsecure = false with get, set

  // --- exchange ---
  member val Exchanges = [| "BitBank" |] with get, set
  // --- ---

  member val TargetIncomingLiquidityRatio = 50s<percent> with get, set

  member this.GetLndGrpcSettings() =
    LndGrpcSettings.Create(
      this.LndGrpcServer,
      this.LndMacaroon |> Option.ofObj,
      this.LndMacaroonFilePath |> Option.ofObj,
      this.LndCertThumbPrint |> Option.ofObj,
      this.LndAllowInsecure
    )
    |>
      function
        | Ok x -> x
        | Error e -> failwith $"Invalid Lnd config: {e}"


