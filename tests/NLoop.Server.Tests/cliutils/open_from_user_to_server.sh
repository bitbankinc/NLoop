#!/usr/bin/env bash

set -eu

server_pubkey=$(curl http://localhost:6028/getnodes | jq ".nodes.BTC.nodeKey")

feerate=$(lightning-cli --network=regtest feerates perkw | jq ".perkw.opening")
lightning-cli --network=regtest fundchannel $server_pubkey 500000 $feerate false

./docker-bitcoin-cli.sh generatetoaddress 3 bcrt1qjwfqxekdas249pr9fgcpxzuhmndv6dqlulh44m
