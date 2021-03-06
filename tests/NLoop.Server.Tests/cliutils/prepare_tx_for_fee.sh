#!/usr/bin/env bash

set -e
./docker-bitcoin-cli.sh getnewaddress "" bech32 | xargs  -IXX ./docker-bitcoin-cli.sh generatetoaddress 120 XX

for i in `seq 0 6`; do
  for j in `seq 0 6`; do
    ./docker-bitcoin-cli.sh sendtoaddress bcrt1qjwfqxekdas249pr9fgcpxzuhmndv6dqlulh44m 0.01 
  done
  ./docker-bitcoin-cli.sh generatetoaddress 1 bcrt1qh2fa77h3zqc09yz3ydvfgtvrwarapn38a6n69n
done

# Connect peers
./docker-lncli-user.sh getinfo | jq ".uris[0]" | xargs -IXX ./docker-lncli-server.sh connect XX  

# send on-chain funds to both sides.
./docker-lncli-user.sh newaddress p2wkh | jq -r ".address" | xargs -IXX ./docker-bitcoin-cli.sh sendtoaddress XX 1
./docker-lncli-server.sh newaddress p2wkh | jq -r ".address" | xargs -IXX ./docker-bitcoin-cli.sh sendtoaddress XX 1
./docker-bitcoin-cli.sh generatetoaddress 6 bcrt1qjwfqxekdas249pr9fgcpxzuhmndv6dqlulh44m # Just for confirmation

