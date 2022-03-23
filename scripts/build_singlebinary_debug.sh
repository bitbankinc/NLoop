#!/usr/bin/env bash

set -eu

RID=""
if [[ $(uname) = Darwin ]]; then
  RID=osx-x64
else
  RID=linux-x64
fi

SCRIPT_DIR="$( cd -- "$(dirname "$0")" >/dev/null 2>&1 ; pwd -P )"
output_dir="${SCRIPT_DIR}/../tests/NLoop.Server.Tests/Dockerfiles/publish"

#output_dir="publish"
echo "publishing into ${output_dir} for rid: ${RID}...\n"

dotnet publish -c Debug \
    -o $output_dir \
    -p:PublishReadyToRun=true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    -p:RuntimeIdentifier=$RID \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    --self-contained true \
    NLoop.Server \

