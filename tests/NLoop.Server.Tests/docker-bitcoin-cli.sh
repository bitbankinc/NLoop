#!/bin/bash

docker-compose exec -T bitcoind bitcoin-cli -datadir="/data" -rpcport=43782 -rpcpassword=unsafepassword -rpcuser=johndoe "$@"  | sed 's/\t//g'
