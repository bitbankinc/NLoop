#!/bin/bash

docker-compose exec lnd_server_btc lncli --tlscertpath=/data/tls.cert --macaroonpath=/data/admin.macaroon --rpcserver=localhost:32778 $@
