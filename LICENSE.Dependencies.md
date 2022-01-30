# Dependent Softwares and its licenses.

## list

| Name of the software | Its license | Copyright | Usage in NLoop |
| :---: | :---: | :---: | :---: |
| [EventStoreDB][esdb] | [3-Clause BSD License][esdb-license] |  | To store everything including Swap state, secret keys, etc. |
| [NBitcon][nbitcoin] | [MIT License][nbitcoin-license] |  | To use Bitcoin-related types |
| [DotNetLightning][dnl] | MIT License |  | To use LN-related primitive types | 
| [FsToolKit.ErrorHandling][fstoolkit] | [MIT license][fstoolkit-license] | | For handling `Result`, `TaskResult` effectively |
| [FSharp.Contro.AsyncSeq][asyncseq] |  [Apache License 2.0](asyncseq-license) |   | To use asynchronous sequences |
| [FSharp.SystemTextJson][fsharp-stj] |  [MIT license][fsharp-stj-license] |  | For Json (de)serialization of F# Specific types such as Records and Unions
| [DigiralRuby.ExchangeSharp][exchangesharp] | [MIT license][exchangesharp-license] | | To query the currency's exchange rate against the server API. |
| [FSharp.Control.Reactive][fcr] | [MSPL][fcr-license] | | For handling Reactive Extensions in F#-native way |
| [Giraffe][giraffe] | [Apache License 2.0][giraffe-license] | | For web API | [NetMQ][netmq] | [LGPL][netmq-license] | | For listening to bitcoind's zeromq interface | 
| [Ply][ply] | [MIT License][ply-license] |  | For handling `task` in F# CE |
| [System.Commandline][system-commandline] | [MIT License][system-commandline-license] | | For parsing commandline options |
| [NReco.Logging.File][nreco.logging] | [MIT License][nreco.logging-license] | | For logging into the file |
| [ameier38/ouroboros][ouroboros] | [MIT License][ouroboros-license] | | It does not depend directly, but an idea of how to implement an event-sourcing is heavily influenced by this project. |


[esdb]:https://github.com/EventStore/EventStore
[esdb-license]: https://github.com/EventStore/EventStore/blob/master/LICENSE.md
[nbitcoin]: https://github.com/MetacoSA/NBitcoin
[nbitcoin-license]: https://github.com/MetacoSA/NBitcoin/blob/master/LICENSE
[dnl]: https://github.com/joemphilips/DotNetLightning
[fstoolkit]: https://github.com/demystifyfp/FsToolkit.ErrorHandling
[fstoolkit-license]: https://github.com/demystifyfp/FsToolkit.ErrorHandling/blob/master/License
[asyncseq]: https://github.com/fsprojects/FSharp.Control.AsyncSeq
[asyncseq-license]: https://github.com/fsprojects/FSharp.Control.AsyncSeq/blob/main/LICENSE.md
[fsharp-stj]: https://github.com/Tarmil/FSharp.SystemTextJson
[fsharp-stj-license]: https://github.com/Tarmil/FSharp.SystemTextJson/blob/master/LICENSE
[exchangesharp]: https://github.com/techsoft3d/ExchangeSharp
[exchangesharp-license]: https://github.com/jjxtra/ExchangeSharp/blob/master/LICENSE.txt
[fcr]: https://github.com/fsprojects/FSharp.Control.Reactive
[fcr-license]: https://github.com/fsprojects/FSharp.Control.Reactive/blob/master/LICENSE.txt
[giraffe]: https://github.com/giraffe-fsharp/Giraffe
[giraffe-license]: https://github.com/giraffe-fsharp/Giraffe/blob/master/LICENSE
[netmq]: https://github.com/zeromq/netmq
[netmq-license]: https://github.com/zeromq/netmq/blob/master/COPYING.LESSER
[ply]: https://github.com/crowded/ply
[ply-license]: https://github.com/crowded/ply/blob/master/LICENSE.md
[system-commandline]: https://github.com/dotnet/command-line-api
[system-commandline-license]: https://github.com/dotnet/command-line-api/blob/main/LICENSE.md
[nreco.logging]: https://github.com/nreco/logging
[nreco.logging-license]: https://github.com/nreco/logging/blob/master/LICENSE
[ouroboros]: https://github.com/ameier38/ouroboros
[ouroboros-license]: https://github.com/ameier38/ouroboros/blob/master/LICENSE
