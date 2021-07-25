namespace rec NLoop.OpenAPIClient.FSharp

open System.Net
open System.Net.Http
open System.Text
open NLoop.OpenAPIClient.FSharp.Types
open NLoop.OpenAPIClient.FSharp.Http
open FSharp.Control.Tasks

///Lightning Channel Manager. It will maintain the channel balance by performing submarine swap against Boltz server.
type ``NLoop.OpenAPIClient.FSharpClient``(httpClient: HttpClient) =
    member this.version() =
        task {
            let requestParts = []
            let! (status, content) = OpenApiHttp.getAsync httpClient "/v1/version" requestParts
            return Version.OK content
        }

    member this.out(body: LoopOutRequest) =
        task {
            let requestParts = [ RequestPart.jsonContent body ]
            let! (status, content) = OpenApiHttp.postAsync httpClient "/v1/{cryptoCode}/loop/out" requestParts
            return Out.OK(Serializer.deserialize content)
        }

    member this.``in``(body: LoopInRequest) =
        task {
            let requestParts = [ RequestPart.jsonContent body ]
            let! (status, content) = OpenApiHttp.postAsync httpClient "/v1/{cryptoCode}/loop/in" requestParts
            return In.OK(Serializer.deserialize content)
        }
