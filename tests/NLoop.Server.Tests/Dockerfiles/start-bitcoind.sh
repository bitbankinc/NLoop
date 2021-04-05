#!/usr/bin/env bash

# exit from script if error was raised.
set -e

# error function is used within a bash function in order to send the error
# message directly to the stderr output and exit.
error() {
    echo "$1" > /dev/stderr
    exit 0
}

# return is used within bash function in order to return the value.
return() {
    echo "$1"
}

# set_default function gives the ability to move the setting of default
# env variable from docker file to the script thereby giving the ability to the
# user override it durin container start.
set_default() {
    # docker initialized env variables with blank string and we can't just
    # use -z flag as usually.
    BLANK_STRING='""'

    VARIABLE="$1"
    DEFAULT="$2"

    if [[ -z "$VARIABLE" || "$VARIABLE" == "$BLANK_STRING" ]]; then

        if [ -z "$DEFAULT" ]; then
            error "You should specify default variable"
        else
            VARIABLE="$DEFAULT"
        fi
    fi

   return "$VARIABLE"
}


# We need at least 1 block for lnd to create wallet.
create_blocks() {
  while
    bitcoin-cli -$1 generatetoaddress 1 bcrt1qjwfqxekdas249pr9fgcpxzuhmndv6dqlulh44m
    if [[ "$?" == 0 ]]; then
      break
    else
      echo "Failed to create new block... starting in seconds."
      sleep 1
    fi
  do true; done
}

BITCOIN_RPC_AUTH=$(set_default "$BITCOIN_RPC_AUTH" "devuser")
BITCOIN_NETWORK=$(set_default "$BITCOIN_NETWORK" "regtest")

create_blocks ${BITCOIN_NETWORK} &

exec bitcoind \
    -${BITCOIN_NETWORK}
    -rpcauth=${BITCOIN_RPC_AUTH}
    "$@"

