namespace NLoop.Server

open Newtonsoft.Json
open System.Text.Json
open DotNetLightning.Utils
open NBitcoin
open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Domain.Utils

type [<Measure>] percent
type [<Measure>] ppm

type ComparableOutpoint = uint256 * uint

exception NLoopConfigException of msg: string

[<AutoOpen>]
module NLoopServerHelpers =
  let getChainOptionString (chain: SupportedCryptoCode) (optionSubSectionName: string) =
    $"--{chain.ToString().ToLowerInvariant()}.{optionSubSectionName.ToLowerInvariant()}"

  [<Literal>]
  let private FeeBase = 1_000_000L

  let ppmToSat (amount: Money, ppm: int64<ppm>): Money =
    Money.Satoshis(amount.Satoshi * (ppm |> int64) / FeeBase)

  let inline convertDTOToNLoopCompatibleStyle (input: 'TIn) =
    let json =
      let deserializeSettings = JsonSerializerSettings(NullValueHandling = NullValueHandling.Include, MissingMemberHandling = MissingMemberHandling.Error)
      JsonConvert.SerializeObject(input, deserializeSettings)
    let opts = JsonSerializerOptions(IgnoreNullValues = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    opts.AddNLoopJsonConverters()
    JsonSerializer.Deserialize<'T>(json, opts)
  let inline convertDTOToJsonRPCStyle (input: 'TIn) : 'T =
    let json =
      let opts = JsonSerializerOptions(IgnoreNullValues = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
      opts.AddNLoopJsonConverters()
      JsonSerializer.Serialize<'TIn>(input, opts)
    let deserializeSettings = JsonSerializerSettings(NullValueHandling = NullValueHandling.Include, MissingMemberHandling = MissingMemberHandling.Error)
    JsonConvert.DeserializeObject<'T>(json, deserializeSettings)


[<Struct>]
type TargetPeerOrChannel = {
  Peer: NodeId
  Channels: ShortChannelId array
}
