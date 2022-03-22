
#!/usr/bin/env bash

set -eu

RID=""
if [[ $(uname -a) = Darwin ]]; then
  RID=osx-x64
else
  RID=linux-x64
fi

output_dir="publish"
echo "publishing into ${output_dir}..."

dotnet publish -c Debug \
    -o $output_dir \
    -p:PublishReadyToRun=true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true \
    -p:RuntimeIdentifier=$RID \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    --self-contained true \
    NLoop.Server \

