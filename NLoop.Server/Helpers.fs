namespace NLoop.Server

open DotNetLightning.Utils
open NBitcoin
open NLoop.Domain
open NLoop.Domain.Utils

type [<Measure>] percent
type [<Measure>] ppm

type ComparableOutpoint = uint256 * uint

exception NLoopConfigException of msg: string

[<AutoOpen>]
module internal Helpers =
  let getChainOptionString (chain: SupportedCryptoCode) (optionSubSectionName: string) =
    $"--{chain.ToString().ToLowerInvariant()}.{optionSubSectionName.ToLowerInvariant()}"

  [<Literal>]
  let private FeeBase = 1_000_000L

  let ppmToSat (amount: Money, ppm: int64<ppm>): Money =
    Money.Satoshis(amount.Satoshi * (ppm |> int64) / FeeBase)


[<Struct>]
type TargetPeerOrChannel = {
  Peer: NodeId
  Channels: ShortChannelId array
}

[<RequireQualifiedAccess>]
module List =
    let rec private traverseResultM'
        (state: Result<_, _>)
        (f: _ -> Result<_, _>)
        xs
        =
        match xs with
        | [] -> state
        | x :: xs ->
            let r =
                f x
                |> Result.bind(fun y ->
                  state
                  |> Result.map(fun ys ->
                    ys @ [y]
                  )
                )
            match r with
            | Ok _ -> traverseResultM' r f xs
            | Error _ -> r

    let traverseResultM f xs =
        traverseResultM' (Ok []) f xs

    let sequenceResultM xs =
        traverseResultM id xs

    let rec private traverseResultA' state f xs =
        match xs with
        | [] -> state
        | x :: xs ->
            let fR = f x |> Result.mapError List.singleton

            match state, fR with
            | Ok ys, Ok y -> traverseResultA' (Ok(ys @ [ y ])) f xs
            | Error errs, Error e -> traverseResultA' (Error(errs @ e)) f xs
            | Ok _, Error e
            | Error e, Ok _ -> traverseResultA' (Error e) f xs

    let traverseResultA f xs =
        traverseResultA' (Ok []) f xs

    let sequenceResultA xs =
        traverseResultA id xs
