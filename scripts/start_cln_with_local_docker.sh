#!/usr/bin/env bash

set -eu

lightningd \
  --network=regtest \
  --nloop-nohttps=true \
  --bitcoin-rpcuser=johndoe \
  --bitcoin-rpcpassword=unsafepassword \
  --bitcoin-rpcconnect=localhost \
  --bitcoin-rpcport=43782 \
  --nloop-ltc.rpcuser=johndoe \
  --nloop-ltc.rpcpassword=unsafepassword \
  --nloop-ltc.rpchost=localhost \
  --nloop-ltc.rpcport=43783 \
  --nloop-eventstoreurl tcp://admin:changeit@localhost:1113 \
  --nloop-boltzhost http://localhost \
  --nloop-boltzport 6028 \
  --nloop-exchanges FTX

