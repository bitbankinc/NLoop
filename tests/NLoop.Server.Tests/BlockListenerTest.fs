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

type BlockListenerTest() =
  [<Fact>]
  member this.TestBlockListener() =
    use server = new TestServer(TestHelpers.GetTestHost(fun services ->
      services
        .AddSingleton<RPCBlockchainListener>()
        |> ignore
    ))

    let listener = server.Services.GetRequiredService<RPCBlockchainListener>()
    Assert.NotNull(listener)
    ()


[<AutoOpen>]
module private ZmqListenerTestHelper =
  let privKey1 = new Key(hex.DecodeData("0101010101010101010101010101010101010101010101010101010101010101"))
  let privKey2 = new Key(hex.DecodeData("0202020202020202020202020202020202020202020202020202020202020202"))
  let privKey3 = new Key(hex.DecodeData("0303030303030303030303030303030303030303030303030303030303030303"))
  let privKey4 = new Key(hex.DecodeData("0404040404040404040404040404040404040404040404040404040404040404"))
  let privKey5 = new Key(hex.DecodeData("0505050505050505050505050505050505050505050505050505050505050505"))
  let privKey6 = new Key(hex.DecodeData("0606060606060606060606060606060606060606060606060606060606060606"))
  let pubkey1 = privKey1.PubKey
  let pubkey2 = privKey2.PubKey
  let pubkey3 = privKey3.PubKey
  let pubkey4 = privKey4.PubKey
  let pubkey5 = privKey5.PubKey
  let pubkey6 = privKey6.PubKey

  type BlockWithHeight with
    member this.CreateNext(addr: BitcoinAddress) =
      let nextHeight = this.Height + BlockHeightOffset16.One
      {
        Block = this.Block.CreateNextBlockWithCoinbase(addr, nextHeight.Value |> int)
        Height = nextHeight
      }
type ZmqBlockListenerTest() =

  [<Fact>]
  member this.FindCommonAncestorTest() =
    let b0 = {
      Block = Network.RegTest.GetGenesis()
      Height = BlockHeight.Zero
    }
    ()

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

       let b1 =
         let coinbase = pubkey1.WitHash.GetAddress(Network.RegTest)
         b0.CreateNext(coinbase)
       let b2_1 =
         pubkey2.WitHash.GetAddress(Network.RegTest)
         |> b1.CreateNext
       let b2_2 =
         pubkey3.WitHash.GetAddress(Network.RegTest)
         |> b1.CreateNext
       let b3_1 =
         pubkey4.WitHash.GetAddress(Network.RegTest)
         |> b2_1.CreateNext
       let b3_2 =
         pubkey5.WitHash.GetAddress(Network.RegTest)
         |> b2_2.CreateNext

       ("Genesis block must be ignored", seq [b0],seq [], seq [])
       ("Next block must be committed", seq[b0; b1], seq[], seq [b1])
       ("Competing blocks in the same height must be both committed", seq [b0; b1; b2_1; b2_2], seq[], seq [b1; b2_1; b2_2])
       ("Competing blocks in the same height (2blocks each)", seq [b0; b1; b2_1; b3_1; b2_2; b3_2], seq[], seq [b1; b2_1; b3_1; b2_2; b3_2])
       ("Reorg", seq[ b0; b1; b2_1; b2_2; b3_2], seq[], seq [b1; b2_1; b2_2; b3_2 ])
       ("Block has been skipped.", seq [b0; b1; b2_1; b3_1;], seq[ b2_1 ], seq[ b1; b2_1; b3_1 ])
       ("Block has been skipped and reorged", seq [b0; b1; b2_1; b2_2; b3_2], seq[b2_2], seq[b1; b2_1; b2_2; b3_2])
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
    let mockSwapActor = {
      new ISwapActor with
        member this.ExecNewLoopOut(req, currentHeight) =
          failwith "todo"
        member this.ExecNewLoopIn(req, currentHeight) =
          failwith "todo"
        member this.Handler =
          failwith "todo"
        member this.Aggregate =
          failwith "todo"
        member this.Execute(swapId, msg, source) =
          match msg with
          | Swap.Command.NewBlock(h, b, cc) ->
            actualBlocks.Add({ Height = h; Block = b })
          | x ->
            failwith $"Unexpected command type {x}"
          Task.CompletedTask
        member this.GetAllEntities ct =
          failwith "todo"
    }
    // we want to assure that GetBlockHash RPC call will return the value only when the block is in the active chain.
    // thus we must first filter blocks that is only in an active chain.
    let activeChain =
      let tips =
        let highest = inputBlocks |> Seq.maxBy(fun b -> b.Height)
        inputBlocks |> Seq.filter(fun b -> b.Height = highest.Height)
      let activeChain = ResizeArray()
      activeChain.AddRange tips
      for b in inputBlocks |> Seq.rev do
        if activeChain |> Seq.exists(fun activeBlock -> activeBlock.Block.Header.HashPrevBlock = b.Block.Header.GetHash()) then
          activeChain.Add b
      activeChain |> Seq.rev
    let mockBlockChainClient = {
      new IBlockChainClient with
        member this.GetBlock(blockHash, _) = task {
          let mutable ret = None
          for b in inputBlocks do
            if b.Block.Header.GetHash() = blockHash then
              ret <- Some b
            else
              ()
          return if ret.IsNone then failwith $"no block found for {blockHash}" else ret.Value
          }
        member this.GetBlockChainInfo(_) =
          failwith "todo"
        member this.GetBlockHash(height, _) =
          activeChain
          |> Seq.pick(fun activeBlock -> if activeBlock.Height = height then Some(activeBlock.Block.Header.GetHash()) else None)
          |> Task.FromResult
        member this.GetRawTransaction(txid, ct) =
          failwith "todo"
        member this.GetBestBlockHash(ct) =
          inputBlocks |> Seq.last |> fun b -> b.Block.Header.GetHash() |> Task.FromResult
    }
    use server = new TestServer(TestHelpers.GetTestHost(fun services ->
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
        if blocksToSkip |> Seq.contains b |> not then
          do! listener.OnBlock(SupportedCryptoCode.BTC, b.Block) |> Async.StartAsTask

      Assert.Equal<BlockWithHeight list>(expectedBlocks |> Seq.toList, actualBlocks |> Seq.toList)
    }


