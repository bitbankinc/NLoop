namespace NLoop.Server.Tests

open NLoop.Server
open Xunit

type JsonRpcServerTest() =
    
    [<Fact>]
    member this.CanHandlePluginOptionsCorrectly() =
        let o = "onchaincrypto"
        let v = PluginOptions.addPrefix(o)
        
        Assert.Equal(v, "nloop-onchaincrypto")
        
        let removed = PluginOptions.removePrefix(v)
        Assert.Equal(removed, "onchaincrypto")
        let removed2 = PluginOptions.removePrefix($"--{v}")
        Assert.Equal(removed2, "onchaincrypto")
        ()

