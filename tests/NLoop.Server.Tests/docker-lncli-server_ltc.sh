#!/bin/bash

docker-compose exec -T lnd_server_ltc lncli \
  --network=regtest \
  --chain=litecoin \
  --tlscertpath=/data/tls.cert \
  --macaroonpath=/data/admin.macaroon \
  --rpcserver=localhost:32778 $@
