#!/usr/bin/env bash

set -eu

if [[ pgrep -x lightningd ]]; then
  user_pubkey=$(lightning-cli --network=regtest getinfo | jq -r ".id")
elif
  user_pubkey=$(./docker-lncli-user.sh getinfo | jq -r ".identity_pubkey")
fi

./docker-lncli-server_btc.sh openchannel --private $user_pubkey 500000
./docker-bitcoin-cli.sh generatetoaddress 4 bcrt1qjwfqxekdas249pr9fgcpxzuhmndv6dqlulh44m

