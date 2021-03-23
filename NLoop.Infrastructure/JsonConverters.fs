namespace NLoop.Infrastructure

open System
open System.Runtime.CompilerServices
open System.Text.Json
open System.Text.Json.Serialization
open BTCPayServer.Lightning
open DotNetLightning.Payment
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

type UnixTimeJsonConverter() =
  inherit JsonConverter<DateTimeOffset>()
  override this.Write(writer, value, _options) =
    NBitcoin.Utils.DateTimeToUnixTime(value)
    |> writer.WriteNumberValue
  override this.Read(reader, _typeToConvert, _options) =
    reader.GetUInt32() |> NBitcoin.Utils.UnixTimeToDateTime

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

type PaymentRequestJsonConverter() =
  inherit JsonConverter<PaymentRequest>()
  override this.Write(writer, value, _options) =
    value.ToString() |> writer.WriteStringValue
  override this.Read(reader, _typeToConvert, _options) =
    let s = reader.GetString()
    PaymentRequest.Parse s
    |> ResultUtils.Result.defaultWith (fun () -> raise <| JsonException())

type BitcoinAddressJsonConverter(n: Network) =
  inherit JsonConverter<BitcoinAddress>()
  override this.Write(writer, value, _options) =
    value.ToString() |> writer.WriteStringValue
  override this.Read(reader, _typeToConvert, _options) =
    reader.GetString() |> fun s -> BitcoinAddress.Create(s, n)

type PairIdJsonConverter() =
  inherit JsonConverter<PairId>()
  override this.Write(writer, value, _options) =
    let bid, ask = value
    $"{bid.CryptoCode.ToUpperInvariant()}/{ask.CryptoCode.ToUpperInvariant()}"
    |> writer.WriteStringValue
  override this.Read(reader, _typeToConvert, _options) =

    let v = reader.GetString()
    let s = v.Split("/")
    if (s.Length <> 2) then raise <| JsonException() else
    let fromString s =
      match s with
      | "BTC" -> Bitcoin.Instance :> INetworkSet
      | s -> raise <| JsonException($"Unknown network {s}")

    (fromString s.[0], fromString s.[1])

type ScriptJsonConverter() =
  inherit JsonConverter<Script>()
  override this.Write(writer, value, _options) =
    value.ToHex() |> writer.WriteStringValue
  override this.Read(reader, _typeToConvert, _options) =
    reader.GetString() |> Script.FromHex

type ShortChannelIdJsonConverter() =
  inherit JsonConverter<ShortChannelId>()
  override this.Write(writer, value, _options) =
    value.AsString |> writer.WriteStringValue
  override this.Read(reader, _typeToConvert, _options) =
    reader.GetString() |> ShortChannelId.ParseUnsafe

[<AbstractClass;Sealed;Extension>]
type Extensions() =
  [<Extension>]
  static member AddNLoopJsonConverters(this: JsonSerializerOptions, chainName: ChainName) =
    this.Converters.Add(HexPubKeyJsonConverter())
    this.Converters.Add(BlockHeightJsonConverter())
    this.Converters.Add(UInt256JsonConverter())
    this.Converters.Add(MoneyJsonConverter())
    this.Converters.Add(PaymentRequestJsonConverter())
    this.Converters.Add(PairIdJsonConverter())
    this.Converters.Add(BitcoinAddressJsonConverter(Bitcoin.Instance.GetNetwork chainName))
    this.Converters.Add(HexTxConverter(Bitcoin.Instance.GetNetwork chainName))
    this.Converters.Add(PeerConnectionStringJsonConverter())
    this.Converters.Add(ShortChannelIdJsonConverter())
    this.Converters.Add(JsonFSharpConverter(JsonUnionEncoding.FSharpLuLike))
