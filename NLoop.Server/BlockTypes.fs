namespace NLoop.Server

open DotNetLightning.Utils
open NBitcoin

[<StructuredFormatDisplay("{AsString}")>]
type BlockWithHeight = {
  Block: Block
  Height: BlockHeight
}
  with
  static member Genesis(n: Network) = {
    Block = n.GetGenesis()
    Height = BlockHeight.Zero
  }
  override this.ToString() = $"(height: {this.Height.Value}, block: {this.Block.Header.GetHash().ToString().[..7]}...)"
  member this.AsString = this.ToString()

[<RequireQualifiedAccess>]
module BlockWithHeight =
  /// Given the following blockchain
  /// block0 ----> block1 ---> block2_0 ---> block3_0
  ///                   \
  ///                     -----> block2_1 ----> block3_1
  /// `rewindToNextOfCommonAncestor (getBlock) block3_0 block3_1`
  /// will return Some `block2_1`.
  ///
  /// `rewindToNextOfCommonAncestor (getBlock) block1 block3_0`
  /// will return Some `block2_0`.
  ///
  /// If there were no common ancestor, it will return None, this should never happen if the blocks are from the same
  /// blockchain.
  let rec rewindToNextOfCommonAncestor(getBlock: uint256 -> Async<BlockWithHeight>) (oldTip: BlockWithHeight) (newTip: BlockWithHeight) = async {
    assert(oldTip.Block.Header.GetHash() <> newTip.Block.Header.GetHash())
    if oldTip.Block.Header.HashPrevBlock = newTip.Block.Header.HashPrevBlock then
      return newTip |> Some
    elif oldTip.Block.Header.GetHash() = newTip.Block.Header.HashPrevBlock then
      return newTip |> Some
    elif oldTip.Height <= newTip.Height then
      let! newTipPrev = getBlock newTip.Block.Header.HashPrevBlock
      return! rewindToNextOfCommonAncestor getBlock oldTip newTipPrev
    elif oldTip.Height > newTip.Height then
      let! oldTipPrev = getBlock oldTip.Block.Header.HashPrevBlock
      return! rewindToNextOfCommonAncestor getBlock oldTipPrev newTip
    else
      return failwith "unreachable!"
  }
