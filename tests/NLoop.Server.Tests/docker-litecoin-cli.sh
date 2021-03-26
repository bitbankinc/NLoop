#!/bin/bash

docker-compose exec -T litecoind litecoin-cli -regtest -datadir="/data" -rpcport=${LITECOIND_RPC_PORT} -rpcpassword=unsafepassword -rpcuser=johndoe "$@"
