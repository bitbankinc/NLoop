## NLoop Regtest guide (c-lightning)

You can try running `nloopd` as a plugin for c-lightning in the
similar [way we did with lnd](./lnd_regtest.md)

But since c-lightning does not work well with docker as lnd does, we run
`lightningd` on the host machine.

* First, download [c-lightning binary](https://github.com/ElementsProject/lightning/releases), or [compile by yourself](https://github.com/ElementsProject/lightning/blob/master/doc/INSTALL.md).
* Next, download [`nloopd` binary](https://github.com/bitbankinc/NLoop/releases/tag/v1.2.0.0-beta) or [compile by yourself](./compile.md)
* run all other dependencies as we did in lnd ... `cd tests/NLoop.Server.Tests/ && docker-compose up -d`
* Run `./scripts/start_cln_with_local_docker.sh` in project root, this will launch `lightningd` in regtest mode with using nloopd binary in your PATH as a plugin.
  * It uses appropriate startup option for talking to dependent services.
  * You can check all plugin startup options by
`lightningd --plugin=/path/to/nloopd -help`
* Now you can talk to lightningd as usual
  * all RPC methods for nloop has a prefix `nloop-`, run `lightning-cli --network=regtest help` to see those RPCs.
  * It must be the same with those of [the rest api](https://bitbankinc.github.io/NLoop/)


Now check `lightningd` is running correctly with nloopd as a plugin with
`lightning-cli --network=regtest nloop_getinfo`

For example executing a loopout can be done by
`lightning-cli --network=regtest nloop_loopout '{ "amount": "50000sat", "max_swap_fee": 2050 }'`

You can use utility scripts under `tests/NLoop.Server.Tests/cliutils` in the same way we did for lnd.

```sh
cd tests/NLoop.Server.Tests
./cliutils/prepare_funds.sh
./cliutils/prepare_tx_for_fee.sh
./cliutils/open_from_user_to_server.sh
```

And also to reset the state 

```sh
cd tests/NLoop.Server.Tests
docker-compose down
rm -rf data
git checkout -- data
# Also, don't forget to clean your lightning-dir for lightningd!
```