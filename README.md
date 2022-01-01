# NLoop: Generic Lightning Loop

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

## REST API

Check out our [`openapi.yml`](./openapi.yml) for the REST API specification.

There is a one endpoint which is not included in the spec.
That is a WebSocket endpoint for listening to events.
* `/v1/events`

## Why use NLoop?

### Server side is open source.



### Event Sourcing

* Immutable audit trail with WORM
* Cache invalidation

### Extensive tests
