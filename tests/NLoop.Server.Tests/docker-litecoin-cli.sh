#!/bin/bash

docker-compose exec litecoind litecoin-cli -regtest -datadir="/data" -rpcport=43783 -rpcpassword=unsafepassword -rpcuser=johndoe "$@"
