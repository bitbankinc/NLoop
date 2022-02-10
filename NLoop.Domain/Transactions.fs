namespace NLoop.Domain

open System
open System.IO
open DotNetLightning.Utils.Primitives
open NBitcoin
open NBitcoin
open NBitcoin.DataEncoders
open FsToolkit.ErrorHandling
open NBitcoin.DataEncoders

[<RequireQualifiedAccess>]
module Transactions =
  type Error =
    | RedeemScriptMismatch of actualSpks: Script seq * expectedRedeem: Script
    with
    member this.Message =
      match this with
      | RedeemScriptMismatch(actualSpks, expectedRedeem) ->
        $"""Transaction did not contain expected redeem script.
        (actual scriptPubKeys in lockup TX: "{String.Join('\n', actualSpks |> Seq.map(fun s -> s.ToHex()))}")
        (expected redeem script: "{expectedRedeem.ToHex()}")
        (expected ScriptPubKey (p2wsh): {expectedRedeem.WitHash.ScriptPubKey.ToHex()})
        (expected ScriptPubKey (p2sh-p2wsh): {expectedRedeem.WitHash.ScriptPubKey.Hash.ScriptPubKey.ToHex()})
        """
    override this.ToString() = this.Message

  /// We might bump the claim tx, so this returns an RBF enabled tx.
  let createClaimTx
    (output: BitcoinAddress)
    (key: Key)
    (preimage: PaymentPreimage)
    (redeemScript: Script)
    (feeRate: FeeRate)
    (lockupTx: Transaction)
    (n: Network) =
    let createClaimTxFromTransactionBuilder(setFeeOp: TransactionBuilder -> TransactionBuilder) =
      let txB = n.CreateTransactionBuilder()
      let coins = lockupTx.Outputs.AsCoins()
      let mutable sc = null
      for c in coins do
        if (c.TxOut.ScriptPubKey = redeemScript.WitHash.ScriptPubKey || c.TxOut.ScriptPubKey = redeemScript.WitHash.ScriptPubKey.Hash.ScriptPubKey) then
          sc <- ScriptCoin(c, redeemScript)
          txB.AddCoins(sc) |> ignore
      if (sc |> isNull) then
        let actualOutputs = lockupTx.Outputs |> Seq.map(fun o -> o.ScriptPubKey)
        Error(RedeemScriptMismatch(actualOutputs, redeemScript))
      else
        let tx =
          setFeeOp(txB)
            .SendAllRemaining(output)
            .SetOptInRBF(true)
            .BuildTransaction(false)
        // TransactionBuilder does not support non-standard script as we use here.
        // Thus we should not sign by passing true to the BuildTransaction.
        // Instead, we have to sign manually by Tx.SignInput(key, coins),
        if sc.TxOut.ScriptPubKey = redeemScript.WitHash.ScriptPubKey.Hash.ScriptPubKey then
          // Building Transaction without signature will result to empty ScriptSig in case of p2sh-p2wsh.
          // Thus creating manually here.
          let script = Script([| Op.op_Implicit OpcodeType.OP_0; Op.GetPushOp(redeemScript.WitHash.ToBytes())|])
          tx.Inputs.[0].ScriptSig <- Script([|Op.GetPushOp(script.ToBytes())|])
        let signature = tx.SignInput(key, sc)
        let witnessItems =
          WitScript(Op.GetPushOp(signature.ToBytes())) +
            WitScript(Op.GetPushOp(preimage.ToByteArray())) +
            WitScript(Op.GetPushOp(redeemScript.ToBytes()))
        tx.Inputs.[0].WitScript <- witnessItems
        Ok tx
    result {
      let! dummyTx =
        createClaimTxFromTransactionBuilder(fun txB -> txB.SendEstimatedFees(feeRate))
      let feeAdjusted =
        feeRate.GetFee(dummyTx.GetVirtualSize())
      let! tx = createClaimTxFromTransactionBuilder(fun txB -> txB.SendFees(feeAdjusted))
      return tx
    }

  let createSwapPSBT
    (inputs: ICoin seq)
    (redeemScript: Script)
    (outputAmount: Money)
    (feeRate: FeeRate)
    (change: IDestination)
    (n: Network)
    =
    if (outputAmount.Satoshi < 0L) then Error("Negative amount for swap output") else
    let whatWeHave = inputs |> Seq.sumBy(fun i -> i.TxOut.Value)
    if (whatWeHave < outputAmount) then Error($"Insufficient funds (what we have: {whatWeHave.Satoshi} satoshi, output: {outputAmount.Satoshi} satoshis)") else
    let psbt =
      n.CreateTransactionBuilder()
        .AddCoins(inputs)
        .SendEstimatedFees(feeRate)
        .Send(redeemScript.WitHash, outputAmount)
        .SetChange(change)
        .BuildPSBT(false)
    psbt.AddScripts(redeemScript)
    |> Ok

  let private coinBase = Network.RegTest.CreateTransaction()
  let private dummyCoin =
    coinBase.Outputs
      .Add(TxOut(Money.Coins(1.1m), Scripts.pubkey1)) |> ignore
    coinBase.Outputs.AsCoins()
    |> Seq.cast<ICoin>
  let dummySwapTx feeRate =
    let dummyChange = PubKey("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619")
    createSwapPSBT
      dummyCoin
      Scripts.dummySwapScriptV1
      (Money.Coins(1m))
      feeRate
      dummyChange
      Network.RegTest
    |> Result.map(fun psbt ->
      psbt
        .AddTransactions(coinBase)
        .SignWithKeys(Scripts.privKey1)
        .Finalize()
        .ExtractTransaction()
      )
    |> Result.valueOr failwith

  let dummySwapTxFee feeRate =
    let swapTx = dummySwapTx feeRate
    swapTx.GetFee(dummyCoin |> Seq.toArray)

  let createRefundTx
    (lockupTxHex: string)
    (redeemScript: Script)
    fee
    (refundAddress: IDestination)
    (refundKey: Key)
    (timeout: BlockHeight)
    n =
    let swapTx =
      lockupTxHex
      |> fun hex -> Transaction.Parse(hex, n)
    let coins =
      swapTx.Outputs.AsCoins()
    let mutable sc = null
    let txb =
      n.CreateTransactionBuilder()
    for c in coins do
      if (c.TxOut.ScriptPubKey = redeemScript.WitHash.ScriptPubKey || c.TxOut.ScriptPubKey = redeemScript.WitHash.ScriptPubKey.Hash.ScriptPubKey) then
        sc <- ScriptCoin(c, redeemScript)
        txb.AddCoins(sc) |> ignore
    if (sc |> isNull) then
      let actualOutputs = swapTx.Outputs |> Seq.map(fun o -> o.ScriptPubKey)
      Error(RedeemScriptMismatch(actualOutputs, redeemScript))
    else
      let tx =
        txb
          .SendEstimatedFees(fee)
          .SendAll(refundAddress)
          .SetOptInRBF(true)
          .SetLockTime(timeout.Value |> LockTime)
          .AddKeys(refundKey)
          .BuildTransaction(false)
      // TransactionBuilder does not support non-standard script as we use here.
      // Thus we should not sign by passing true to the BuildTransaction.
      // Instead, we have to sign manually by Tx.SignInput(key, coins),
      if sc.TxOut.ScriptPubKey = redeemScript.WitHash.ScriptPubKey.Hash.ScriptPubKey then
        // Building Transaction without signature will result to empty ScriptSig in case of p2sh-p2wsh.
        // Thus creating manually here.
        let script = Script([| Op.op_Implicit OpcodeType.OP_0; Op.GetPushOp(redeemScript.WitHash.ToBytes())|])
        tx.Inputs.[0].ScriptSig <- Script([|Op.GetPushOp(script.ToBytes())|])
      let signature = tx.SignInput(refundKey, sc)
      let witnessItems =
        WitScript(Op.GetPushOp(signature.ToBytes())) +
          WitScript(Op.GetPushOp([||])) +
          WitScript(Op.GetPushOp(redeemScript.ToBytes()))
      tx.Inputs.[0].WitScript <- witnessItems
      tx
      |> Ok

  let hex = HexEncoder()
  let dummyRefundTx feeRate =
    let prev = dummySwapTx feeRate
    let refundKey =
      "4141414141414141414141414141414141414141414141414141414141414141"
      |> hex.DecodeData
      |> fun h -> new Key(h)
    let refundAddr = refundKey.PubKey.WitHash.GetAddress(Network.RegTest)
    createRefundTx
      (prev.ToHex())
      Scripts.dummySwapScriptV1
      feeRate
      refundAddr
      refundKey
      BlockHeight.One
      Network.RegTest
    |> Result.valueOr(fun e -> failwith $"Failed create dummy fund tx: {e.Message}")
  let dummyRefundTxFee feeRate =
    let prev = dummySwapTx feeRate
    dummyRefundTx feeRate
    |> fun t ->
      let swapOutput = prev.Outputs.AsCoins() |> Seq.find(fun o -> o.ScriptPubKey = Scripts.dummySwapScriptV1.WitHash.ScriptPubKey)
      swapOutput.Amount - t.TotalOut
