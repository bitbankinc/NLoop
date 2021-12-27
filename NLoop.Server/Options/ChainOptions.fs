namespace NLoop.Server.Options

open System
open System.Runtime.CompilerServices
open DotNetLightning.Utils
open NBitcoin
open NBitcoin.RPC
open NLoop.Domain


type IChainOptions =
  abstract member CryptoCode: SupportedCryptoCode with get, set

  // --- on-chain node host ---
  abstract member RPCHost: string with get, set
  abstract member RPCPort: int with get, set
  abstract member RPCUser: string with get, set
  abstract member RPCPassword: string with get, set
  abstract member RPCCookieFile: string with get, set
  // --- ---

  // --- zeromq ---
  abstract member ZmqHost: string with get, set
  abstract member ZmqPort: string with get, set
  // --- ---

  // --- swap params ---

  /// Confirmation target for the sweep in on-chain swap
  abstract member SweepConf: int with get, set

  // --- properties and methods ---

[<Extension;AbstractClass;Sealed>]
type IChainOptionExtensions =
  [<Extension>]
  static member GetNetwork(this: IChainOptions, chainName: string) =
    this.CryptoCode.ToNetworkSet().GetNetwork(ChainName chainName)

  [<Extension>]
  static member GetRPCClient(this: IChainOptions, chainName: string) =
    RPCClient($"{this.RPCUser}:{this.RPCPassword}", $"{this.RPCHost}:{this.RPCPort}", this.GetNetwork(chainName))

  [<Extension>]
  static member GetZmqAddress(this: IChainOptions) = $"tcp://{this.ZmqHost}:{this.ZmqPort}"

type BTCChainOptions(n: Network) =
  new () = BTCChainOptions(Network.RegTest)
  static member val Instance = BTCChainOptions() with get
  interface IChainOptions with
    member val CryptoCode = SupportedCryptoCode.BTC with get, set

    // --- on-chain node host ---
    member val RPCHost = "localhost" with get, set
    member val RPCPort = n.RPCPort with get, set
    member val RPCUser = String.Empty with get, set
    member val RPCPassword = String.Empty with get, set
    member val RPCCookieFile = String.Empty with get, set
    // --- ---

    // --- zeromq ---
    member val ZmqHost = "localhost" with get, set
    member val ZmqPort = "28332" with get, set
    // --- ---

    // --- swap params ---

    /// Confirmation target for the sweep in on-chain swap
    member val SweepConf = 6 with get, set


type LTCChainOptions(n: Network) =
  new () = LTCChainOptions(Altcoins.Litecoin.Instance.Regtest)
  static member val Instance = LTCChainOptions() with get
  interface IChainOptions with
    member val CryptoCode = SupportedCryptoCode.LTC with get, set

    // --- on-chain node host ---
    member val RPCHost = "localhost" with get, set
    member val RPCPort = n.RPCPort with get, set
    member val RPCUser = String.Empty with get, set
    member val RPCPassword = String.Empty with get, set
    member val RPCCookieFile = String.Empty with get, set
    // --- --

    // --- zeromq ---
    member val ZmqHost = "localhost" with get, set
    member val ZmqPort = "28332" with get, set
    // --- ---

    // --- swap params ---

    /// Confirmation target for the sweep in on-chain swap
    member val SweepConf = 18 with get, set

