#!/usr/bin/env bash

set -eu

user_pubkey=$(lightning-cli --network=regtest getinfo | jq -r ".id")

./docker-lncli-server_btc.sh openchannel --private $user_pubkey 500000

./docker-bitcoin-cli.sh generatetoaddress 3 bcrt1qjwfqxekdas249pr9fgcpxzuhmndv6dqlulh44m

