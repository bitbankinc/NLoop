#!/bin/bash

docker-compose exec -T litecoind litecoin-cli -regtest -datadir="/data" -rpcport=43783 -rpcpassword=unsafepassword -rpcuser=johndoe "$@" | sed 's/\t//g'
