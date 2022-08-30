#!/usr/bin/env bash

set -eu

cln_datadir=`pwd`/tests/NLoop.Server.Tests/data/lightning_user
nloopd=`command -v nloopd`

lightning-cli \
  --lightning-dir=$cln_datadir \
  --network=regtest plugin stop nloopd

dotnet publish  NLoop.Server \
  -p:PublishReadyToRun=true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=false \
  -p:RuntimeIdentifier=linux-x64 \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  --self-contained true

lightning-cli \
  --lightning-dir=$cln_datadir \
  --network=regtest \
  -k plugin subcommand=start \
  plugin=$nloopd \
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

