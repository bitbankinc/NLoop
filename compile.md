### How to compile nloopd.

It is faily simple to compile nloopd by yourself.
All you need is to install latest dotnet sdk (>= 6)
and run

```
dotnet publish  NLoop.Server \
  -p:PublishReadyToRun=true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=false \
  -p:RuntimeIdentifier=linux-x64 \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  --self-contained true
```

`RuntimeIdentifier` must be modified according to the machine you want to run.

