namespace NLoop.Domain

open System
open System.Net
open System.Runtime.CompilerServices
open System.Text.Json.Serialization
open DotNetLightning.Utils
open NBitcoin
open NBitcoin.Altcoins

type PeerConnectionString = {
  NodeId: PubKey
  EndPoint: EndPoint
}
  with
  override this.ToString() =
    $"{this.NodeId.ToHex()}@{this.EndPoint.ToEndpointString()}"

  static member TryParse(str: string) =
    if (str |> isNull) then raise <| ArgumentNullException(nameof(str)) else
    let s = str.Split("@")
    if (s.Length <> 2) then Error("No @ symbol") else
    let nodeId = PubKey(s.[0])
    let addrAndPort = s.[1].Split(":")
    if (addrAndPort.Length <> 2) then Error("no : symbol in between address and port") else

    match Int32.TryParse(addrAndPort.[1]) with
    | false, _ -> Error($"Failed to parse {addrAndPort.[1]} as port")
    | true, port ->

    let endPoint =
      match IPAddress.TryParse(addrAndPort.[0]) with
      | true, ipAddr -> IPEndPoint(ipAddr, port) :> EndPoint
      | false, _ -> DnsEndPoint(addrAndPort.[0], port) :> EndPoint

    {
      NodeId = nodeId
      EndPoint = endPoint
    } |> Ok

  static member Parse(str: string) =
    match PeerConnectionString.TryParse str with
    | Ok r -> r
    | Error e -> raise <| FormatException($"Invalid connection string ({str}). {e}")

[<JsonConverter(typeof<JsonStringEnumConverter>)>]
type SupportedCryptoCode =
  | BTC = 0uy
  | LTC = 1uy

[<AbstractClass;Sealed;Extension>]
type Ext() =
  [<Extension>]
  static member ToNetworkSet(this: SupportedCryptoCode) =
    match this with
    | SupportedCryptoCode.BTC -> Bitcoin.Instance :> INetworkSet
    | SupportedCryptoCode.LTC -> Litecoin.Instance :> INetworkSet
    | _ -> failwith $"Unreachable! {this}"

[<RequireQualifiedAccess>]
module SupportedCryptoCode =
  let TryParse (s: string) =
    match s.ToUpperInvariant() with
    | "BTC" -> SupportedCryptoCode.BTC |> Some
    | "LTC" -> SupportedCryptoCode.LTC |> Some
    | _ -> None

[<Struct>]
type PairId =
  PairId of (struct (SupportedCryptoCode * SupportedCryptoCode))
  with
  member this.Reverse =
    let struct (b, q) = this.Value
    PairId(q, b)
  member this.Value =
    match this with
    | PairId(a, b) -> struct(a, b)

  member this.Base = let struct (b, _)= this.Value in b
  member this.Quote = let struct(_, q) = this.Value in q

[<RequireQualifiedAccess>]
module PairId =
  let Default = PairId(SupportedCryptoCode.BTC, SupportedCryptoCode.BTC)

  let toString (pairId: inref<PairId>) =
    let struct (a, b)= pairId.Value
    $"{a}/{b}"

  let toStringFromVal(pairId: PairId) =
    toString(&pairId)

type SwapId = SwapId of string
  with
  member this.Value = let (SwapId v) = this in v
  override this.ToString() =
    this.Value

[<StructuredFormatDisplay("{AsString}")>]
type BlockWithHeight = {
  Block: Block
  Height: BlockHeight
}
  with
  static member Genesis(n: Network) = {
    Block = n.GetGenesis()
    Height = BlockHeight.Zero
  }
  member this.Copy() = {
    Block = this.Block.Clone()
    Height = this.Height.Value |> BlockHeight
  }

  override this.ToString() = $"(height: {this.Height.Value}, block: {this.Block.Header.GetHash().ToString().[..7]}...)"
  member this.AsString = this.ToString()
