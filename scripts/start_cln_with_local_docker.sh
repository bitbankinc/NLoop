#!/usr/bin/env bash

set -eu

bitcoin_datadir=`pwd`/tests/NLoop.Server.Tests/data/bitcoin
cln_datadir=`pwd`/tests/NLoop.Server.Tests/data/lightning_user
nloopd=`command -v nloopd`

lightningd \
  --network=regtest \
  --nloop-nohttps=true \
  --plugin=$nloopd \
  --lightning-dir=$cln_datadir \
  --bitcoin-datadir=$bitcoin_datadir \
  --bitcoin-rpcuser=johndoe \
  --bitcoin-rpcpassword=unsafepassword \
  --bitcoin-rpcconnect=localhost \
  --bitcoin-rpcport=43782 \
  --log-level=debug:nloop \
  --nloop-ltc.rpcuser=johndoe \
  --nloop-ltc.rpcpassword=unsafepassword \
  --nloop-ltc.rpchost=localhost \
  --nloop-ltc.rpcport=43783 \
  --nloop-btc.rpcuser=johndoe \
  --nloop-btc.rpcpassword=unsafepassword \
  --nloop-btc.rpchost=localhost \
  --nloop-btc.rpcport=43782 \
  --nloop-eventstoreurl tcp://admin:changeit@localhost:1113 \
  --nloop-boltzhost http://localhost \
  --nloop-boltzport 6028 \
  --nloop-exchanges FTX

