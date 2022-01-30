[<AutoOpen>]
module NLoop.Tests.CommonExtensions

open DotNetLightning.Utils
open NBitcoin
open NLoop.Domain

type BlockWithHeight with
  member this.CreateNext(addr: BitcoinAddress) =
    let nextHeight = this.Height + BlockHeightOffset16.One
    {
      Block = this.Block.CreateNextBlockWithCoinbase(addr, nextHeight.Value |> int)
      Height = nextHeight
    }


  member this.CreateInfinite(network: Network) =
    Seq.unfold(fun (b: BlockWithHeight) ->
      let addr = (new Key()).PubKey.WitHash.GetAddress(network)
      let next = b.CreateNext(addr)
      (b, next) |> Some
      )
      this
  member this.CreateNextMany(network: Network, n: int) =
    this.CreateInfinite(network)
    |> Seq.take n


