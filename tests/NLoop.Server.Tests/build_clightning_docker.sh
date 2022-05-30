#!/usr/bin/env bash

set -eu

nloop_version=$(git rev-parse --short=7 HEAD)
docker build -f ../../NLoop.Server/Dockerfile ../.. -t nloopd:$nloop_version
docker-compose -f docker-compose.yml -f docker-compose.clightning.yml build
