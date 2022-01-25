#!/bin/bash

docker-compose exec lnd_user lncli --tlscertpath=/data/tls.cert --macaroonpath=/data/admin.macaroon --rpcserver=localhost:32777 $@
