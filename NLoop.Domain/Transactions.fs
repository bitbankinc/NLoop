namespace NLoop.Domain

open System
open System.IO
open DotNetLightning.Utils.Primitives
open NBitcoin
open NBitcoin.DataEncoders
open FsToolkit.ErrorHandling

[<RequireQualifiedAccess>]
module Transactions =
  type Error =
    | RedeemScriptMismatch of actualSpks: Script seq * expectedRedeem: Script
    with
    member this.Message =
      match this with
      | RedeemScriptMismatch(actualSpks, expectedRedeem) ->
        $"""Transaction did not contain expected redeem script.
        (actual scriptPubKeys: {String.Join('\n', actualSpks)})
        (redeemScript: {expectedRedeem.ToHex()})
        (expected ScriptPubKey (p2wsh): {expectedRedeem.WitHash.ScriptPubKey.ToHex()})
        (expected ScriptPubKey (p2sh-p2wsh): {expectedRedeem.WitHash.ScriptPubKey.Hash.ScriptPubKey.ToHex()})
        """
  let createClaimTx
    (output: BitcoinAddress)
    (key: Key)
    (preimage: uint256)
    (redeemScript: Script)
    (fee:  FeeRate)
    (lockupTx: Transaction)
    (n: Network) =
    let txB = n.CreateTransactionBuilder()
    let coins = lockupTx.Outputs.AsCoins()
    let mutable sc = null
    for c in coins do
      if (c.TxOut.ScriptPubKey = redeemScript.WitHash.ScriptPubKey || c.TxOut.ScriptPubKey = redeemScript.WitHash.ScriptPubKey.Hash.ScriptPubKey) then
        sc <- ScriptCoin(c, redeemScript)
        txB.AddCoins() |> ignore
    if (sc |> isNull) then
      let actualOutputs = lockupTx.Outputs |> Seq.map(fun o -> o.ScriptPubKey)
      Error(RedeemScriptMismatch(actualOutputs, redeemScript))
    else
      let tx =
        txB
          .SendEstimatedFees(fee)
          .SendAll(output)
          .BuildTransaction(false)
      let signature = tx.SignInput(key, sc)
      let witnessItems =
        WitScript(Op.GetPushOp(signature.ToBytes())) +
          WitScript(Op.GetPushOp(preimage.ToBytes())) +
          WitScript(Op.GetPushOp(redeemScript.ToBytes()))
      tx.Inputs.[0].WitScript <- witnessItems
      Ok tx

  let createSwapPSBT
    (inputs: ICoin seq)
    (redeemScript: Script)
    (outputAmount: Money)
    (feeRate: FeeRate)
    (change: IDestination)
    (timeout: BlockHeight)
    (n: Network)
    =
    if (outputAmount.Satoshi < 0L) then Error("Negative amount for swap output") else
    let whatWeHave = inputs |> Seq.sumBy(fun i -> i.TxOut.Value)
    if (whatWeHave < outputAmount) then Error($"Insufficient funds (what we have: {whatWeHave.Satoshi} satoshi, output: {outputAmount.Satoshi} satoshis)") else
    let txb = n.CreateTransactionBuilder()
    txb
      .AddCoins(inputs)
      .SendEstimatedFees(feeRate)
      .Send(redeemScript.WitHash.ScriptPubKey, outputAmount)
      .SetChange(change)
      .SetLockTime(timeout.Value |> LockTime)
      .BuildPSBT(false)
      |> Ok
