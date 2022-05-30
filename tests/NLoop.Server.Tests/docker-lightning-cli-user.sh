#!/bin/bash

docker-compose -f docker-compose.yml -f docker-compose.clightning.yml exec -T clightning_user lightning-cli --rpc-file /root/.lightning/lightning-rpc --network regtest --lightning-dir /root/.lightning $@

