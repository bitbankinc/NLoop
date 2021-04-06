namespace NLoop.Server

open System.IO
open DotNetLightning.Serialization
open DotNetLightning.Utils.Primitives
open NBitcoin
open NBitcoin.DataEncoders
open NBitcoin.DataEncoders
open NLoop.Server.DTOs

type SwapUpdateEvent = int

type LoopOut = {
  Id: string
  State: SwapStatusType
  Error: string
  Status: SwapUpdateEvent
  AcceptZeroConf: bool
  PrivateKey: Key
  Preimage: uint256
  RedeemScript: Script
  Invoice: string
  ClaimAddress: string
  OnChainAmount: int64
  TimeoutBlockHeight: BlockHeight
  LockupTransactionId: uint256
  ClaimTransactionId: uint256
}
  with
  static member Deserialize(ls: LightningReaderStream) =
    let ascii = ASCIIEncoder()
    {
      Id = ls.ReadWithLen() |> ascii.EncodeData
      State = ls.ReadByte() |> LanguagePrimitives.EnumOfValue
      Error = ls.ReadWithLen() |> ascii.EncodeData
      Status = ls.ReadInt32(false)
      AcceptZeroConf = ls.ReadBoolean()
      PrivateKey = ls.ReadBytes(32) |> Key
      Preimage = ls.ReadUInt256(true)
      RedeemScript = ls.ReadScript()
      Invoice = ls.ReadWithLen() |> ascii.EncodeData
      ClaimAddress = ls.ReadWithLen() |> ascii.EncodeData
      OnChainAmount = ls.ReadInt64(false)
      TimeoutBlockHeight = ls.ReadUInt32(false) |> BlockHeight
      LockupTransactionId = ls.ReadUInt256(true)
      ClaimTransactionId = ls.ReadUInt256(true) }
  member this.Serialize(ls : LightningWriterStream) =
    let ascii = ASCIIEncoder()
    if this.Id |> isNull then ls.Write(0uy) else this.Id |> ascii.DecodeData |> ls.WriteWithLen
    this.State |> LanguagePrimitives.EnumToValue |> ls.WriteByte
    if this.Id |> isNull then ls.Write(0uy) else this.Error |> ascii.DecodeData |> ls.WriteWithLen
    ls.Write(this.Status , false)
    ls.Write(if this.AcceptZeroConf then 1uy else 0uy)
    ls.Write(this.PrivateKey.ToBytes())
    ls.Write(this.Preimage, true)
    ls.WriteWithLen(this.RedeemScript.ToBytes())
    if this.Id |> isNull then ls.Write(0uy) else this.Invoice |> ascii.DecodeData |> ls.WriteWithLen
    if this.Id |> isNull then ls.Write(0uy) else this.ClaimAddress |> ascii.DecodeData |> ls.WriteWithLen
    ls.Write(this.OnChainAmount,false)
    ls.Write(this.TimeoutBlockHeight.Value, false)
    ls.Write(this.LockupTransactionId, true)
    ls.Write(this.ClaimTransactionId, true)
    ()

  static member FromBytes(b: byte[]) =
    use m = new MemoryStream(b)
    use ls = new LightningReaderStream(m)
    LoopOut.Deserialize(ls)
  member this.ToBytes() =
    use m = new MemoryStream()
    use ls = new LightningWriterStream(m)
    this.Serialize(ls)
    m.ToArray()


/// TODO
type LoopIn = {
  Id: string
}
