namespace NLoop.Server

open NBitcoin

type NLoopServerConfig = {
  Network: ChainName
  DBPath: string
  MaxAcceptableSwapFee: Money
}

