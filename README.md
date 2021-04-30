# NLoop: Lightning Loop against boltz-backend


## How to try with local docker-compose environment

```sh
cd tests/NLoop.Server.Tests
source env.sh
docker-compose up # Start dependencies such as bitcoind and lnd
cd ../..
./scripts/start_with_local_docker.sh

# Get general information about NLoop.
curl http://localhost:5000/v1/info
```
