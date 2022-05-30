#!/usr/bin/env bash

set -eu

docker-compose -f docker-compose.yml -f docker-compose.clightning.yml build
