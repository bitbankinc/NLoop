namespace NLoop.Infrastructure

open NBitcoin

type NLoopServerConfig = {
  Network: ChainName
  DBPath: string
  MaxAcceptableSwapFee: Money
}

