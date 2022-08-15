#!/usr/bin/env bash

lightning-cli --network=regtest plugin stop nloopd

set -eu

dotnet publish  NLoop.Server   -p:PublishReadyToRun=true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    -p:RuntimeIdentifier=linux-x64 \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    --self-contained true

lightning-cli --network=regtest -k plugin subcommand=start \
  plugin=/home/joemphilips/working/sandbox/fsharp/NLoop/NLoop.Server/bin/Debug/net6.0/linux-x64/nloopd \
  nloop-nohttps=true \
  nloop-btc.rpcuser=johndoe \
  nloop-btc.rpcpassword=unsafepassword \
  nloop-btc.rpchost=localhost \
  nloop-btc.rpcport=43782 \
  nloop-ltc.rpcuser=johndoe \
  nloop-ltc.rpcpassword=unsafepassword \
  nloop-ltc.rpchost=localhost \
  nloop-ltc.rpcport=43783 \
  nloop-eventstoreurl=tcp://admin:changeit@localhost:1113 \
  nloop-boltzhost=http://localhost \
  nloop-boltzport=6028 \
  nloop-exchanges=FTX

