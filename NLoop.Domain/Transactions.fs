namespace NLoop.Server

open System.IO
open NBitcoin
open NBitcoin.DataEncoders

module Transactions =
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
    if (sc |> isNull) then raise <| InvalidDataException($"Redeem Script mismatch for tx {lockupTx} and script {redeemScript.ToHex()}") else
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
    tx
