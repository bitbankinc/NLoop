namespace NLoop.Infrastructure.Swap

open System.Collections.Generic
open DotNetLightning.Utils.Primitives
open NBitcoin
open NBitcoin.Crypto

module private Helpers =
  let reverseSwapScriptV1(preimageHash: PaymentHash) (claimPubKey: PubKey) (refundPubKey: PubKey) (timeout: BlockHeight) =
    let l = List<Op>()
    l.Add(Op.op_Implicit (OpcodeType.OP_SIZE))
    l.Add(Op.GetPushOp(32L))
    l.Add(Op.op_Implicit (OpcodeType.OP_EQUAL))
    l.Add(Op.op_Implicit (OpcodeType.OP_IF))
    l.Add(Op.op_Implicit (OpcodeType.OP_HASH160))
    l.Add(Op.GetPushOp(preimageHash.Value.ToBytes() |> Hashes.RIPEMD160))
    l.Add(Op.op_Implicit (OpcodeType.OP_EQUALVERIFY))
    l.Add(Op.GetPushOp(claimPubKey.ToBytes()))
    l.Add(Op.op_Implicit (OpcodeType.OP_ELSE))
    l.Add(Op.op_Implicit (OpcodeType.OP_DROP))
    l.Add(Op.GetPushOp(timeout.Value |> int64))
    l.Add(Op.op_Implicit (OpcodeType.OP_CHECKLOCKTIMEVERIFY))
    l.Add(Op.op_Implicit (OpcodeType.OP_DROP))
    l.Add(Op.GetPushOp(refundPubKey.ToBytes()))
    l.Add(Op.op_Implicit (OpcodeType.OP_ENDIF))
    l.Add(Op.op_Implicit (OpcodeType.OP_CHECKSIG))
    Script(l)

  let swapScriptV1 (preimageHash: PaymentHash) (claimPubKey: PubKey) (refundPubKey: PubKey) (timeout: BlockHeight)  =
    let l = List<Op>()
    l.Add(Op.op_Implicit (OpcodeType.OP_HASH160))
    l.Add(Op.GetPushOp(preimageHash.Value.ToBytes() |> Hashes.RIPEMD160))
    l.Add(Op.op_Implicit (OpcodeType.OP_EQUAL))
    l.Add(Op.op_Implicit (OpcodeType.OP_IF))
    l.Add(Op.GetPushOp(claimPubKey.ToBytes()))
    l.Add(Op.op_Implicit (OpcodeType.OP_ELSE))
    l.Add(Op.GetPushOp(timeout.Value |> int64))
    l.Add(Op.op_Implicit (OpcodeType.OP_CHECKLOCKTIMEVERIFY))
    l.Add(Op.op_Implicit (OpcodeType.OP_DROP))
    l.Add(Op.GetPushOp(refundPubKey.ToBytes()))
    l.Add(Op.op_Implicit (OpcodeType.OP_ENDIF))
    l.Add(Op.op_Implicit (OpcodeType.OP_CHECKSIG))
    Script(l)

type HTLCScript =
  | V1
  | UnknownVersion
  with
  member this.GetSuccessWitness =
    failwith "TODO"
  member this.Script =
    match this with
    | V1 ->
      failwith ""


type HTLC = {
  HTLCScript: Script
}
