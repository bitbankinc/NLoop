#!/usr/bin/env bash

set -e

LndVersion=v0.12.1-beta
rpc_swagger_json=$(mktemp).json
invoices_swagger_json=$(mktemp).json

echo "rpc_swagger_json: $rpc_swagger_json"
echo "invoices_swagger_json: $invoices_swagger_json"

curl "https://raw.githubusercontent.com/lightningnetwork/lnd/${LndVersion}/lnrpc/rpc.swagger.json" > $rpc_swagger_json
curl "https://raw.githubusercontent.com/lightningnetwork/lnd/${LndVersion}/lnrpc/invoicesrpc/invoices.swagger.json" > $invoices_swagger_json

output=LndSwaggerClient/swagger.json


npx swagger-merger \
  -i $invoices_swagger_json \
  -o $rpc_swagger_json

mv $rpc_swagger_json $output

echo "Finished generating: ${output}"
