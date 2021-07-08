#!/usr/bin/env bash

set -e

SCRIPT_DIR="$( cd -- "$(dirname "$0")" >/dev/null 2>&1 ; pwd -P )"
echo $SCRIPT_DIR

admin_macaroon=$(echo ${SCRIPT_DIR}/../tests/NLoop.Server.Tests/data/lnd_user/chain/bitcoin/regtest/admin.macaroon)
certthumbprint=$(openssl x509 -in ${SCRIPT_DIR}/../tests/NLoop.Server.Tests/data/lnd_user/tls.cert -noout -sha256 -fingerprint | cut -f 2 -d "=")

LOGGING__LogLevel__Microsoft=Information


ASPNETCORE_ENVIRONMENT=Development dotnet run --project NLoop.Server -- \
  --network RegTest \
  --nohttps true \
  --btc.lightningconnectionstring "type=lnd-rest;macaroonfilepath="$admin_macaroon";server=https://localhost:32736;certthumbprint="$certthumbprint"" \
  --btc.rpcuser=johndoe \
  --btc.rpcpassword=unsafepassword \
  --btc.rpchost=localhost \
  --btc.rpcport=43782 \
  --ltc.rpcuser=johndoe \
  --ltc.rpcpassword=unsafepassword \
  --ltc.rpchost=localhost \
  --ltc.rpcport=43783 \
  --eventstoreurl tcp://admin:changeit@localhost:1113 \
  --boltzhost https://localhost \
  --boltzport 6028 \
  --boltzhttps false

