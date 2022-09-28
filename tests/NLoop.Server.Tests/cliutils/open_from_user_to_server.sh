#!/usr/bin/env bash

set -eu

server_pubkey=$(curl http://localhost:6028/getnodes | jq ".nodes.BTC.nodeKey")

if pgrep -x lightningd; then
  feerate=$(lightning-cli --network=regtest feerates perkw | jq ".perkw.opening")
  lightning-cli --network=regtest fundchannel $server_pubkey 500000 $feerate false
else
  ./docker-lncli-user.sh openchannel --private $server_pubkey 500000
fi

./docker-bitcoin-cli.sh generatetoaddress 4 bcrt1qjwfqxekdas249pr9fgcpxzuhmndv6dqlulh44m
