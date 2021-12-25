namespace NLoop.Server

open System.Threading
open NBitcoin
open NLoop.Domain


[<RequireQualifiedAccess>]
module BlockWithHeight =
  /// searches for a common ancestor of two blocks, and returns a common ancestor
  /// as its first return value.
  /// The second return value is blocks those which disconnected.
  ///
  /// Given the following blockchain
  /// block0 ----> block1 ---> block2_0 ---> block3_0
  ///                   \
  ///                    -----> block2_1 ----> block3_1
  /// `rewindToNextOfCommonAncestor (getBlock) block3_0 block3_1`
  /// will return `Some(block2_1, [block2_0; block3_0])`.
  ///
  /// `rewindToNextOfCommonAncestor (getBlock) block1 block3_0`
  /// will return `Some(block2_0, [])` .
  ///
  /// If there were no common ancestor, it will return None, this should never happen if the blocks are from the same
  /// blockchain.
  let rewindToNextOfCommonAncestor getBlock oldTip newTip =
    let rec loop
      (getBlock: uint256 -> Async<BlockWithHeight>)
      (blockDisconnected: uint256 list)
      (oldTip: BlockWithHeight)
      (newTip: BlockWithHeight) =
      async {
        let! ct = Async.CancellationToken
        ct.ThrowIfCancellationRequested()
        let oldHash = oldTip.Block.Header.GetHash()
        let newHash = newTip.Block.Header.GetHash()
        assert(oldHash <> newHash)
        if oldTip.Block.Header.HashPrevBlock = newTip.Block.Header.HashPrevBlock then
          let d = oldHash::blockDisconnected
          return (newTip, d) |> Some
        elif oldHash = newTip.Block.Header.HashPrevBlock then
          return (newTip, blockDisconnected) |> Some
        elif oldTip.Height <= newTip.Height then
          let! newTipPrev = getBlock newTip.Block.Header.HashPrevBlock
          return! loop getBlock blockDisconnected oldTip newTipPrev
        elif oldTip.Height > newTip.Height then
          let d = oldHash::blockDisconnected
          let! oldTipPrev = getBlock oldTip.Block.Header.HashPrevBlock
          return! loop getBlock d oldTipPrev newTip
        else
          return failwith "unreachable!"
      }
    loop getBlock [] oldTip newTip
