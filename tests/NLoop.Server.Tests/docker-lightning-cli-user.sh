#!/bin/bash

docker-compose exec -T clightning_user lightning-cli --network regtest --lightning-dir /root/.lightning $@

