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
  abstract member ZmqPort: int with get, set
  // --- ---


  // --- --=
  /// UTXO we have in the wallet must have greater confirmation than this.
  /// Otherwise it is not used as our funds.
  abstract member WalletMinConf: int with get, set
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
  static member TryGetZmqAddress(this: IChainOptions) =
    if this.ZmqHost |> String.IsNullOrEmpty || this.ZmqPort = 0 then
      None
    else
      Some $"tcp://{this.ZmqHost}:{this.ZmqPort}"

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
    member val ZmqHost = null with get, set
    member val ZmqPort = 0 with get, set
    // --- ---

    member val WalletMinConf = 1 with get, set


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
    member val ZmqHost = null with get, set
    member val ZmqPort = 0 with get, set
    // --- ---

    member val WalletMinConf = 3 with get, set
