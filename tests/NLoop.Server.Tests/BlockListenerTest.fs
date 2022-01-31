namespace NLoop.Server.Tests

open System
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open FSharp.Control.Tasks
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open NBitcoin
open NBitcoin.DataEncoders
open NLoop.Domain
open NLoop.Server
open NLoop.Server.Services
open Xunit

[<AutoOpen>]
module private BlockchainListenerTestHelper =

  type BlockWithHeight with
    member this.CreateNext(addr: BitcoinAddress) =
      let nextHeight = this.Height + BlockHeightOffset16.One
      {
        Block = this.Block.CreateNextBlockWithCoinbase(addr, nextHeight.Value |> int)
        Height = nextHeight
      }
type BlockchainListenerTest() =

  static member TestBlockchainListenerTestData: obj[] seq =
     seq {

       // blocks
       // b0 --> b1 --> b2_1 --> b3_1 --> b4_1
       //          \
       //           --> b2_2 --> b3_2 --> b4_2
       let b0 = {
         Block = Network.RegTest.GetGenesis()
         Height = BlockHeight.Zero
       }
       let getNext (b: BlockWithHeight) (pk: PubKey) =
         pk.WitHash.GetAddress(Network.RegTest)
         |> b.CreateNext
       let b1 = getNext b0 pubkey1
       let b2_1 = getNext b1 pubkey2
       let b3_1 = getNext b2_1 pubkey3
       let b4_1 = getNext b3_1 pubkey4

       let b2_2 = getNext b1 pubkey5
       let b3_2 = getNext b2_2 pubkey6
       let b4_2 = getNext b3_2 pubkey7

       ("Genesis block must be ignored", [b0],[], [], [])
       ("Next block must be committed", [b0; b1], [], [b1], [])
       ("Competing blocks in the same height must ignored", [b0; b1; b2_1; b2_2], [], [b1; b2_1], [])
       ("Non-competing (short) fork must be ignored", [b0; b1; b2_1; b3_1; b2_2], [], [b1; b2_1; b3_1], [])
       ("Competing blocks in the same height (2blocks each) must be ignored", [b0; b1; b2_1; b3_1; b2_2; b3_2], [], [b1; b2_1; b3_1], [])
       ("Competing blocks in the same height comes in turn", [b0; b1; b2_1; b2_2; b3_1; b3_2], [], [b1; b2_1; b3_1], [])
       ("Reorg", [ b0; b1; b2_1; b2_2; b3_2], [], [b1; b2_1; b2_2; b3_2 ], [b2_1])

       ("Main chain tip skipped", [b0; b1; b2_1], [b2_1;], [b1], [])
       ("skipped block must be queried when its child comes", [b0; b1; b2_1; b3_1;], [ b2_1 ], [ b1; b2_1; b3_1 ], [])
       ("Non competing (short) fork must be ignored even when the original chain has a skip", [b0; b1; b2_1; b3_1; b2_2], [b2_1], [b1; b2_1; b3_1], [])
       ("Original chain and competing chain both have a skip",  [b0; b1; b2_1; b3_1; b2_2; b3_2;], [b2_1; b2_2], [b1; b2_1; b3_1 ], [])
       ("Original chain and reorged chain's common ancestor has been skipped.",  [b0; b1; b2_1; b2_2; b3_2], [b1], [b1; b2_1; b2_2; b3_2], [b2_1])
       ("Skipped block must be queried even when the reorg happens simultaneously.",  [b0; b1; b2_1; b2_2; b3_2], [b2_2], [b1; b2_1; b2_2; b3_2], [b2_1])

       ("More than one block has been skipped",  [b0; b1; b2_1; b3_1; b4_1], [b2_1; b3_1], [b1; b2_1; b3_1; b4_1], [])
       ("More than one block has been skipped and reorged",  [b0; b1; b2_1; b3_1; b2_2; b3_2; b4_2], [b2_2; b3_2], [b1; b2_1; b3_1; b2_2; b3_2; b4_2], [b2_1; b3_1])
       ("Original chain and reorged chain both have a skip",  [b0; b1; b2_1; b3_1; b2_2; b3_2; b4_2], [b2_1; b2_2; b3_2], [b1; b2_1; b3_1; b2_2; b3_2; b4_2], [b2_1; b3_1])

       let b5_2 = getNext b4_2 pubkey8
       ("Original chain and reorged chain both have a multi-block skip",  [b0; b1; b2_1; b3_1; b4_1; b2_2; b3_2; b4_2; b5_2], [b2_1; b2_2; b3_1; b3_2;], [b1; b2_1; b3_1; b4_1 ;b2_2; b3_2; b4_2; b5_2], [b2_1; b3_1; b4_1])
       let allBlocks = [b0; b1; b2_1; b3_1; b4_1; b2_2; b3_2; b4_2; b5_2]
       let skipped = allBlocks |> List.filter(fun b -> b <> b4_1 && b <> b5_2 && b <> b0)
       let expected =  allBlocks |> List.filter((<>)b0)
       ("Everything is skipped besides two tips", allBlocks, skipped, expected, [b2_1; b3_1; b4_1])

     }
     |> Seq.map(fun (name,
                     inputBlocks: BlockWithHeight list,
                     blocksToSkip: BlockWithHeight list,
                     expectedBlocks: BlockWithHeight list,
                     expectedUnConfirmedBlocks) -> [|
       name |> box
       inputBlocks |> box
       blocksToSkip |> box
       expectedBlocks |> box
       expectedUnConfirmedBlocks |> box
     |])

  [<Theory>]
  [<MemberData(nameof(BlockchainListenerTest.TestBlockchainListenerTestData))>]
  member this.TestBlockListener(_name: string,
                                   inputBlocks: BlockWithHeight list,
                                   blocksToSkip: BlockWithHeight list,
                                   expectedBlocks: BlockWithHeight list,
                                   expectedUnConfirmedBlocks: BlockWithHeight list) =
    let actualBlocks = ResizeArray()
    let blocksOnTheNodeSoFar = ResizeArray()
    let unconfirmedBlocks = ResizeArray()
    use sp = TestHelpers.GetTestServiceProvider(fun services ->
      let mockSwapActor =
        TestHelpers.GetDummySwapActor
          {
            DummySwapActorParameters.Default
            with
              Execute = fun (_swapId, msg, source) ->
                assert (source.Value = nameof(BlockchainListener))
                match msg with
                | Swap.Command.NewBlock(hb, _cc) ->
                  actualBlocks.Add(hb)
                | Swap.Command.UnConfirmBlock(blockHash) ->
                  unconfirmedBlocks.Add(blockHash)
                | x ->
                  failwith $"Unexpected command type {x}"
          }
      let getMockBlockChainClient: GetBlockchainClient = fun _ ->
        TestHelpers.GetDummyBlockchainClient
          {
            DummyBlockChainClientParameters.Default
              with
                GetBlock = fun blockHash ->
                  let mutable ret = None
                  for b in blocksOnTheNodeSoFar do
                    if b.Block.Header.GetHash() = blockHash then
                      ret <- Some b
                    else
                      ()
                  if ret.IsNone then failwith $"no block found for {blockHash}" else ret.Value
                GetBlockHash = fun height ->
                  // we want to assure that GetBlockHash RPC call will return the value only when the block is in the active chain.
                  // thus we must first filter blocks that is only in an active chain.
                  let activeChain =
                    let tips =
                      let highest = blocksOnTheNodeSoFar |> Seq.maxBy(fun b -> b.Height)
                      blocksOnTheNodeSoFar |> Seq.filter(fun b -> b.Height = highest.Height)
                    let activeChain = ResizeArray()
                    activeChain.AddRange tips
                    for b in blocksOnTheNodeSoFar |> Seq.rev do
                      if activeChain |> Seq.exists(fun activeBlock -> activeBlock.Block.Header.HashPrevBlock = b.Block.Header.GetHash()) then
                        activeChain.Add b
                    activeChain |> Seq.rev
                  activeChain
                  |> Seq.pick(fun activeBlock -> if activeBlock.Height = height then Some(activeBlock.Block.Header.GetHash()) else None)
                GetBestBlockHash = fun () ->
                  blocksOnTheNodeSoFar |> Seq.last |> fun b -> b.Block.Header.GetHash()
          }
      services
        .AddSingleton<BlockchainListener>()
        .AddSingleton<ISwapActor>(mockSwapActor)
        .AddSingleton<GetBlockchainClient>(getMockBlockChainClient)
        .AddSingleton<BlockchainListener>(fun sp ->
          BlockchainListener(
            sp.GetRequiredService<_>(),
            sp.GetRequiredService<_>(),
            SupportedCryptoCode.BTC,
            sp.GetRequiredService<_>(),
            sp.GetRequiredService<_>()
          )
        )
        |> ignore
    )
    let listener = sp.GetService<BlockchainListener>()
    Assert.NotNull(listener)
    let dummySwap = SwapId (Guid.NewGuid().ToString())
    listener.RegisterSwap(dummySwap)

    task {
      for b in inputBlocks do
        blocksOnTheNodeSoFar.Add(b)
        if blocksToSkip |> Seq.contains b |> not then
          do! listener.OnBlock(b.Block, fun () -> BlockHeight.Zero)

      Assert.Equal<BlockWithHeight list>(expectedBlocks, actualBlocks |> Seq.toList)
      Assert.Equal<uint256 list>(expectedUnConfirmedBlocks |> List.map(fun b -> b.Block.Header.GetHash()), unconfirmedBlocks |> Seq.toList)
    }
