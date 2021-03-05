namespace NLoop.Infrastructure

open System
open System.Runtime.CompilerServices
open System.Text.Json
open System.Text.Json.Serialization
open DotNetLightning.Utils
open NBitcoin
open NLoop.Infrastructure.DTOs


type PeerConnectionStringJsonConverter() =
  inherit JsonConverter<PeerConnectionString>()

  override this.Write(writer, value, _options) =
    writer.WriteStringValue(value.ToString())
  override this.Read(reader, _typeToConvert, _options) =
    match (PeerConnectionString.TryParse(reader.GetString())) with
    | Ok r -> r
    | Error e ->
      raise <| JsonException e
type HexPubKeyJsonConverter() =
  inherit JsonConverter<PubKey>()

  override this.Write(writer, value, _options) =
    value.ToHex()
    |> writer.WriteStringValue
  override this.Read(reader, _typeToConvert, _options) =
    reader.GetString() |> PubKey

type BlockHeightJsonConverter() =
  inherit JsonConverter<BlockHeight>()
  override this.Write(writer, value, _options) =
    value.Value
    |> writer.WriteNumberValue
  override this.Read(reader, _typeToConvert, _options) =
    reader.GetUInt32() |> BlockHeight

type HexTxConverter(network: Network) =
  inherit JsonConverter<Transaction>()
  override this.Write(writer, value, _options) =
    value.ToHex()
    |> writer.WriteStringValue
  override this.Read(reader, _typeToConvert, _options) =
    reader.GetString() |> fun h -> Transaction.Parse(h, network)


type UInt256JsonConverter() =
  inherit JsonConverter<uint256>()
  override this.Write(writer, value, _options) =
    value
    |> Option.ofObj
    |> Option.iter(fun v -> writer.WriteStringValue(v.ToString()))
  override this.Read(reader, _typeToConvert, _options) =
    reader.GetString() |> uint256.Parse

type MoneyJsonConverter() =
  inherit JsonConverter<Money>()
  override this.Write(writer, value, _options) =
    value.Satoshi |> writer.WriteNumberValue
  override this.Read(reader, _typeToConvert, _options) =
    reader.GetInt64() |> Money.Satoshis

[<AbstractClass;Sealed;Extension>]
type Extensions() =
  [<Extension>]
  static member AddNLoopJsonConverters(this: JsonSerializerOptions, n: NetworkType) =
    this.Converters.Add(HexPubKeyJsonConverter())
    this.Converters.Add(BlockHeightJsonConverter())
    this.Converters.Add(UInt256JsonConverter())
    this.Converters.Add(MoneyJsonConverter())
    this.Converters.Add(HexTxConverter(Bitcoin.Instance.GetNetwork(n)))
    this.Converters.Add(PeerConnectionStringJsonConverter())

