namespace NLoop.Server.Tests

open System
open System.Threading
open System.Threading.Tasks
open DotNetLightning.Utils
open FSharp.Control.Tasks
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open NBitcoin
open NLoop.Domain
open NLoop.Server
open NLoop.Server.Services
open Xunit

[<AutoOpen>]
module private ZmqListenerTestHelper =
  let privKey1 = new Key(hex.DecodeData("0101010101010101010101010101010101010101010101010101010101010101"))
  let privKey2 = new Key(hex.DecodeData("0202020202020202020202020202020202020202020202020202020202020202"))
  let privKey3 = new Key(hex.DecodeData("0303030303030303030303030303030303030303030303030303030303030303"))
  let privKey4 = new Key(hex.DecodeData("0404040404040404040404040404040404040404040404040404040404040404"))
  let privKey5 = new Key(hex.DecodeData("0505050505050505050505050505050505050505050505050505050505050505"))
  let privKey6 = new Key(hex.DecodeData("0606060606060606060606060606060606060606060606060606060606060606"))
  let privKey7 = new Key(hex.DecodeData("0707070707070707070707070707070707070707070707070707070707070707"))
  let pubkey1 = privKey1.PubKey
  let pubkey2 = privKey2.PubKey
  let pubkey3 = privKey3.PubKey
  let pubkey4 = privKey4.PubKey
  let pubkey5 = privKey5.PubKey
  let pubkey6 = privKey6.PubKey
  let pubkey7 = privKey7.PubKey

  type BlockWithHeight with
    member this.CreateNext(addr: BitcoinAddress) =
      let nextHeight = this.Height + BlockHeightOffset16.One
      {
        Block = this.Block.CreateNextBlockWithCoinbase(addr, nextHeight.Value |> int)
        Height = nextHeight
      }
type ZmqBlockListenerTest() =

  static member TestZmqBlockListenerTestData: obj[] seq =
     seq {

       // blocks
       // b0 --> b1 --> b2_1 --> b3_1
       //          \
       //           --> b2_2 --> b3_2
       let b0 = {
         Block = Network.RegTest.GetGenesis()
         Height = BlockHeight.Zero
       }
       let getNext (b: BlockWithHeight) (pk: PubKey) =
         pk.WitHash.GetAddress(Network.RegTest)
         |> b.CreateNext
       let b1 = getNext b0 pubkey1
       let b2_1 = getNext b1 pubkey2
       let b2_2 = getNext b1 pubkey3
       let b3_1 = getNext b2_1 pubkey4
       let b3_2 = getNext b2_2 pubkey5

       ("Genesis block must be ignored", seq [b0],seq [], seq [])
       ("Next block must be committed", seq[b0; b1], seq[], seq [b1])
       ("Competing blocks in the same height must be both committed", seq [b0; b1; b2_1; b2_2], seq[], seq [b1; b2_1; b2_2])
       ("Non competing (short) fork must be ignored", seq [b0; b1; b2_1; b3_1; b2_2], seq[], seq [b1; b2_1; b3_1])
       ("Competing blocks in the same height (2blocks each)", seq [b0; b1; b2_1; b3_1; b2_2; b3_2], seq[], seq [b1; b2_1; b3_1; b2_2; b3_2])
       ("Competing blocks in the same height comes in turn", seq [b0; b1; b2_1;  b2_2; b3_1; b3_2], seq[], seq [b1; b2_1; b2_2; b3_1; b3_2])
       ("Reorg", seq[ b0; b1; b2_1; b2_2; b3_2], seq[], seq [b1; b2_1; b2_2; b3_2 ])

       ("Main chain tip skipped", seq [b0; b1; b2_1], seq[b2_1;], seq[b1])
       ("skipped block must be queried when its child comes", seq [b0; b1; b2_1; b3_1;], seq[ b2_1 ], seq[ b1; b2_1; b3_1 ])
       ("Non competing (short) fork must be ignored even when the original chain has a skip", seq [b0; b1; b2_1; b3_1; b2_2], seq[b2_1], seq [b1; b2_1; b3_1])
       ("Original chain and competing chain both have a skip", seq [b0; b1; b2_1; b3_1; b2_2; b3_2;], seq[b2_1; b2_2], seq[b1; b2_1; b3_1; b2_2; b3_2])
       ("Original chain and competing chain's common ancestor has been skipped.", seq [b0; b1; b2_1; b2_2;], seq[b1], seq[b1; b2_1; b2_2])
       ("Original chain and reorged chain's common ancestor has been skipped.", seq [b0; b1; b2_1; b2_2; b3_2], seq[b1], seq[b1; b2_1; b2_2; b3_2])
       ("Skipped block must be detected when the competing block has come", seq [b0; b1; b2_1; b2_2], seq[b2_1], seq[b1; b2_1; b2_2])
       ("Ancestor and original chain has been skipped entirely (competing)", seq [b0; b1; b2_1; b2_2], seq[b1; b2_1], seq[b1; b2_1; b2_2])
       ("Ancestor and original chain has been skipped entirely (reorged)", seq [b0; b1; b2_1; b2_2; b3_2], seq[b1; b2_1], seq[b1; b2_2; b3_2])
       ("Skipped block must be queried even when the reorg happens simultaneously.", seq [b0; b1; b2_1; b2_2; b3_2], seq[b2_2], seq[b1; b2_1; b2_2; b3_2])

       let b4_2 = getNext b3_2 pubkey6
       ("More than one block has been skipped and reorged", seq [b0; b1; b2_1; b2_2; b3_2; b4_2], seq[b2_2; b3_2], seq[b1; b2_1; b2_2; b3_2; b4_2])
       ("Original chain and reorged chain both have a skip", seq [b0; b1; b2_1; b3_1; b2_2; b3_2; b4_2], seq[b2_1; b2_2; b3_2], seq[b1; b2_1; b3_1; b2_2; b3_2; b4_2])
       let b4_1 = getNext b3_2 pubkey7
       ("Original chain and competing chain both have a multi-block skip", seq [b0; b1; b2_1; b3_1; b4_1; b2_2; b3_2; b4_2], seq[b2_1; b3_1; b2_2; b3_2;], seq[b1; b2_1; b3_1; b4_1; b2_2; b3_2; b4_2])
       ()
     }
     |> Seq.map(fun (name, inputBlocks: BlockWithHeight seq, blocksToSkip: BlockWithHeight seq, expectedBlocks: BlockWithHeight seq) -> [|
       name |> box
       inputBlocks |> box
       blocksToSkip |> box
       expectedBlocks |> box
     |])

  [<Theory>]
  [<MemberData(nameof(ZmqBlockListenerTest.TestZmqBlockListenerTestData))>]
  member this.TestZmqBlockListener(_name: string,
                                   inputBlocks: BlockWithHeight seq,
                                   blocksToSkip: BlockWithHeight seq,
                                   expectedBlocks: BlockWithHeight seq) =
    let actualBlocks = ResizeArray()
    let blocksOnTheNodeSoFar = ResizeArray()
    use server = new TestServer(TestHelpers.GetTestHost(fun services ->
      let mockSwapActor =
        TestHelpers.GetDummySwapActor
          {
            DummySwapActorParameters.Default
            with
              Execute = fun (_swapId, msg, source) ->
                assert (source.Value = nameof(ZmqBlockchainListener))
                match msg with
                | Swap.Command.NewBlock(h, b, _cc) ->
                  actualBlocks.Add({ Height = h; Block = b })
                | x ->
                  failwith $"Unexpected command type {x}"
          }
      let mockBlockChainClient =
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
        .AddSingleton<ZmqBlockchainListener>()
        .AddSingleton<ISwapActor>(mockSwapActor)
        .AddSingleton<IBlockChainClient>(mockBlockChainClient)
        |> ignore
    ))
    let listener = server.Services.GetRequiredService<ZmqBlockchainListener>()
    Assert.NotNull(listener)
    let dummySwap = SwapId (Guid.NewGuid().ToString())
    (listener :> ISwapEventListener).RegisterSwap(dummySwap)

    task {
      for b in inputBlocks do
        blocksOnTheNodeSoFar.Add(b)
        if blocksToSkip |> Seq.contains b |> not then
          do! listener.OnBlock(SupportedCryptoCode.BTC, b.Block) |> Async.StartAsTask

      Assert.Equal<BlockWithHeight list>(expectedBlocks |> Seq.toList, actualBlocks |> Seq.toList)
    }


