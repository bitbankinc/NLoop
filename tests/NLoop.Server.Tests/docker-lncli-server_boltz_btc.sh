#!/bin/bash

docker-compose exec -T lnd_server_boltz_btc lncli --tlscertpath=/data/tls.cert --macaroonpath=/data/admin.macaroon --rpcserver=localhost:32778 $@ | sed 's/\t//g'
