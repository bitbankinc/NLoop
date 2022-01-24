namespace NLoop.Domain.IO

open System
open System.Runtime.CompilerServices
open System.Text.Json
open System.Text.Json.Serialization
open DotNetLightning.Payment
open DotNetLightning.Utils
open NBitcoin
open NBitcoin.DataEncoders
open NLoop.Domain


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

type PrivKeyJsonConverter() =
  inherit JsonConverter<Key>()
  let hex = HexEncoder()

  override this.Write(writer, value, _options) =
    value.ToBytes()
    |> hex.EncodeData
    |> writer.WriteStringValue
  override this.Read(reader, _typeToConvert, _options) =
    reader.GetString() |> hex.DecodeData |> fun x -> new Key(x)

type BlockHeightJsonConverter() =
  inherit JsonConverter<BlockHeight>()
  override this.Write(writer, value, _options) =
    value.Value
    |> writer.WriteNumberValue
  override this.Read(reader, _typeToConvert, _options) =
    reader.GetUInt32() |> BlockHeight

type BlockHeightOffsetJsonConverter() =
  inherit JsonConverter<BlockHeightOffset32>()
  override this.Write(writer, value, _options) =
    value.Value
    |> writer.WriteNumberValue
  override this.Read(reader, _typeToConvert, _options) =
    reader.GetUInt32() |> BlockHeightOffset32


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

type PaymentPreimageJsonConverter() =
  inherit JsonConverter<PaymentPreimage>()
  let hex = HexEncoder()
  override this.Write(writer, value, _options) =
    value.Value
    |> Seq.toArray
    |> hex.EncodeData
    |> writer.WriteStringValue

  override this.Read(reader, _typeToConvert, _options) =
    reader.GetString()
    |> hex.DecodeData
    |> PaymentPreimage.Create

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
    let r = PaymentRequest.Parse s
    r |> ResultUtils.Result.mapError(fun e -> printfn "JsonConverterError: %A" e) |> ignore
    r
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
    let (struct (bid, ask)) = value.Value
    $"{bid.ToString()}/{ask.ToString()}"
    |> writer.WriteStringValue
  override this.Read(reader, _typeToConvert, _options) =
    let v = reader.GetString()
    let s = v.Split("/")
    if (s.Length <> 2) then raise <| JsonException() else
    PairId (SupportedCryptoCode.Parse s.[0], SupportedCryptoCode.Parse s.[1])

type ScriptJsonConverter() =
  inherit JsonConverter<Script>()
  override this.Write(writer, value, _options) =
    value.ToHex() |> writer.WriteStringValue
  override this.Read(reader, _typeToConvert, _options) =
    reader.GetString() |> Script.FromHex

type ShortChannelIdJsonConverter() =
  inherit JsonConverter<ShortChannelId>()
  override this.Write(writer, value, _options) =
    $"{value.AsString}:{value.ToUInt64()}"
    |> writer.WriteStringValue
  override this.Read(reader, _typeToConvert, _options) =
    try
      let s = reader.GetString()
      match ShortChannelId.TryParse s with
      | ResultUtils.Portability.Result.Ok c -> c
      | _ ->
        let parts = s.Split(":").[0]
        match ShortChannelId.TryParse parts with
        | ResultUtils.Portability.Result.Ok c -> c
        | _ ->
        s
        |> uint64
        |> ShortChannelId.FromUInt64
    with
    | ex ->
      reader.GetUInt64()
      |> ShortChannelId.FromUInt64

type SwapIdJsonConverter() =
  inherit JsonConverter<SwapId>()
  override this.Write(writer, value, _options) =
    value.Value |> writer.WriteStringValue
  override this.Read(reader, _typeToConvert, _options) =
    reader.GetString() |> SwapId.SwapId


[<AbstractClass;Sealed;Extension>]
type Extensions() =
  [<Extension>]
  static member AddNLoopJsonConverters(this: JsonSerializerOptions, ?n: Network) =
    this.Converters.Add(HexPubKeyJsonConverter())
    this.Converters.Add(PrivKeyJsonConverter())
    this.Converters.Add(BlockHeightJsonConverter())
    this.Converters.Add(BlockHeightOffsetJsonConverter())
    this.Converters.Add(UInt256JsonConverter())
    this.Converters.Add(MoneyJsonConverter())
    this.Converters.Add(PaymentRequestJsonConverter())
    this.Converters.Add(PairIdJsonConverter())
    this.Converters.Add(PaymentPreimageJsonConverter())

    n |> Option.iter(fun n ->
      this.Converters.Add(BitcoinAddressJsonConverter(n))
      this.Converters.Add(HexTxConverter(n))
    )

    this.Converters.Add(ScriptJsonConverter())
    this.Converters.Add(PeerConnectionStringJsonConverter())
    this.Converters.Add(ShortChannelIdJsonConverter())
    this.Converters.Add(JsonFSharpConverter(JsonUnionEncoding.FSharpLuLike))
