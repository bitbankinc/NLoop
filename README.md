# NLoop: Generic Lightning Loop

## What is NLoop?

It is a submarine swap client against the [boltz-backend](https://github.com/BoltzExchange/boltz-backend)

Currently it imitates the api of the [lightning loop](https://github.com/lightninglabs/loop).

## Why use NLoop?

* Supports liquidity management with autoloop
* Supports multi-asset swap.
* The server side is [boltz](https://github.com/BoltzExchange/boltz-backend), which is OSS. (Which is not the case for lightning loop.)
* Complete immutable audit log with event-sourcing. Which enables you to easily audit how much you have paied as fee during swaps.
* Minimum trust against the server.
* Thorough validation which makes a swap more safe.
* Minimize the direct interaction against the server and instead get the information from the blockchain as much as possible.


## How to

### Quick start

We have a two binaries for you to work with.
* `nloopd` ... standalone daemon to perform/manage the submarine swap.
* `nloop-cli` ... command line tool to work with `nloopd`

Download the latest binary from [the release page](https://github.com/joemphilips/NLoop/releases)
and run with `--help` to see the possible configuration option

`nloopd` must connect to following services to work correctly.

1. [bitcoind](https://github.com/bitcoin/bitcoin)
  * or `litecoind` if you want to work with litecoin.
2. [lnd](https://github.com/bitcoin/bitcoin)
3. [EventStoreDB](https://www.eventstore.com/eventstoredb)
  * For saving the application's state.

Probably the best way to check its behaviour is to run it in the regtest.
Check the following guide for how-to.

### How to build in master

We recommend to use a docker for testing the master version. (To avoid issues of dotnet sdk version incompatibility etc.)
e.g.

```sh
docker build -f NLoop.Server/Dockerfile . -t nloopd_master
docker run nloopd_master --help
```

### How to try `nloopd` with local docker-compose environment

Note that this requires .NET SDK with compatible version installed.

```sh
cd tests/NLoop.Server.Tests
source env.sh
docker-compose up -d # Start dependencies such as bitcoind and lnd
# Or if you need sudo, run with -E option to retain the environment variables.
# `sudo -E docker-compose up -d`

cd ../..
./scripts/start_with_local_docker.sh

# Get general information about NLoop.
curl http://localhost:5000/v1/info
```

To reset the state, you can just run
```sh
cd tests/NLoop.Server.Tests
rm -rf data
git checkout -- data
```

## REST API

Check out our [`openapi.yml`](./openapi.yml) for the REST API specification.

There is a one endpoint which is not included in the spec.
That is a WebSocket endpoint for listening to events.
* `/v1/events`

## Future plans

* [ ] supports swap against lightning-loop-server
* [ ] loop-in autoloop
* [ ] grpc interface

