#!/usr/bin/env bash

set -eu

server_pubkey=$(curl http://localhost:6028/getnodes | jq ".nodes.BTC.nodeKey")

./docker-lncli-user.sh openchannel --private $server_pubkey 500000

feerate=$(./docker-lightning-cli-user.sh feerates perkw | jq ".perkw.opening")
./docker-lightning-cli-user.sh fundchannel $server_pubkey 500000 $feerate false

./docker-bitcoin-cli.sh generatetoaddress 3 bcrt1qjwfqxekdas249pr9fgcpxzuhmndv6dqlulh44m
