#!/bin/bash

docker-compose exec bitcoind bitcoin-cli -datadir="/data" -rpcport=43782 -rpcpassword=unsafepassword -rpcuser=johndoe "$@"
