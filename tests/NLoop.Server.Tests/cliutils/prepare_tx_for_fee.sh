#!/usr/bin/env bash

set -eu

./docker-bitcoin-cli.sh createwallet cashcow 2> /dev/null

# prepare coinbase txes.
./docker-bitcoin-cli.sh getnewaddress "" bech32 | xargs  -IXX ./docker-bitcoin-cli.sh generatetoaddress 120 XX
./docker-litecoin-cli.sh getnewaddress "" bech32 | xargs  -IXX ./docker-litecoin-cli.sh generatetoaddress 120 XX

# generate dummy txes to estimate fee (bitcoin)
for i in `seq 0 6`; do
  for j in `seq 0 6`; do
    ./docker-bitcoin-cli.sh sendtoaddress bcrt1qjwfqxekdas249pr9fgcpxzuhmndv6dqlulh44m 0.01 
  done
  ./docker-bitcoin-cli.sh generatetoaddress 1 bcrt1qh2fa77h3zqc09yz3ydvfgtvrwarapn38a6n69n
done

# generate dummy txes to estimate fee (litecoin)
for i in `seq 0 6`; do
  for j in `seq 0 6`; do
    ./docker-litecoin-cli.sh sendtoaddress QWEEVUpWcCDkriRcv3Yi8eaBT7S2gZtX2H 0.01 
  done
  ./docker-litecoin-cli.sh generatetoaddress 1 QWEEVUpWcCDkriRcv3Yi8eaBT7S2gZtX2H
done


# Connect peers
./docker-lncli-user.sh getinfo | jq ".uris[0]" | xargs -IXX ./docker-lncli-server_btc.sh connect XX  

# send on-chain funds to both sides.
./docker-lncli-user.sh newaddress p2wkh | jq -r ".address" | xargs -IXX ./docker-bitcoin-cli.sh sendtoaddress XX 1
./docker-lncli-server_btc.sh newaddress p2wkh | jq -r ".address" | xargs -IXX ./docker-bitcoin-cli.sh sendtoaddress XX 1
./docker-bitcoin-cli.sh generatetoaddress 6 bcrt1qjwfqxekdas249pr9fgcpxzuhmndv6dqlulh44m # Just for confirmation

# send on-chain litecoin funds for boltz.
./docker-lncli-server_ltc.sh newaddress np2wkh | jq -r ".address" | xargs -IXX ./docker-litecoin-cli.sh sendtoaddress XX 1
./docker-litecoin-cli.sh generatetoaddress 6 QWEEVUpWcCDkriRcv3Yi8eaBT7S2gZtX2H

