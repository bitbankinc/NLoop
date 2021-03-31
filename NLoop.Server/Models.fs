namespace NLoop.Server

open System
open System.Net
open NBitcoin
open ResultUtils

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

type PairId = (INetworkSet * INetworkSet)
