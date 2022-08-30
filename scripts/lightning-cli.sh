#!/usr/bin/env bash

set -eu

cln_datadir=`pwd`/tests/NLoop.Server.Tests/data/lightning_user

lightning-cli \
  --lightning-dir=$cln_datadir \
  --network=regtest \
  $@

