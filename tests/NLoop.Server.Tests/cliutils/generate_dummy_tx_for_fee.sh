#!/usr/bin/env bash

set -eu

# generate dummy txes to estimate fee (bitcoin)
for i in `seq 0 6`; do
    for j in `seq 0 6`; do
      ./docker-bitcoin-cli.sh sendtoaddress bcrt1qjwfqxekdas249pr9fgcpxzuhmndv6dqlulh44m 0.01 
    done
  ./docker-bitcoin-cli.sh generatetoaddress 1 bcrt1qh2fa77h3zqc09yz3ydvfgtvrwarapn38a6n69n
done
