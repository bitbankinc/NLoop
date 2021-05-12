curl http://localhost:6028/getnodes | \
  jq ".nodes.BTC.nodeKey" | \
  xargs -I XX ./docker-lncli-user.sh openchannel --private XX 500000

./docker-bitcoin-cli.sh generatetoaddress 3 bcrt1qjwfqxekdas249pr9fgcpxzuhmndv6dqlulh44m
