module Helpers

open System
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open NBitcoin.DataEncoders
open System.IO
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open BTCPayServer.Lightning
open NBitcoin
open NBitcoin.RPC
open NLoop.Server.Services
open Org.BouncyCastle.Utilities.Net

let getLocalBoltzClient() =
  let httpClient =new  HttpClient()
  httpClient.BaseAddress <- Uri("http://localhost:9001")
  let b = BoltzClient(httpClient)
  b

let private GetCertFingerPrint(filePath: string) =
  use cert = new X509Certificate2(filePath)
  use hashAlg = SHA256.Create()
  hashAlg.ComputeHash(cert.RawData)

let hex = HexEncoder()
let getCertFingerPrintHex (filePath: string) =
  GetCertFingerPrint filePath |> hex.EncodeData
let private checkConnection(port) =
  let l = TcpListener(IPAddress.Loopback, port)
  try
    l.Start()
    l.Stop()
    Ok()
  with
  | :? SocketException -> Error("")

let findEmptyPortUInt(ports: uint []) =
  let mutable i = 0
  while i < ports.Length do
    let mutable port = RandomUtils.GetUInt32() % 4000u
    port <- port + 10000u
    if (ports |> Seq.exists((=)port)) then () else
    match checkConnection((int)port) with
    | Ok _ ->
      ports.[i] <- port
      i <- i + 1
    | _ -> ()
  ports

let findEmptyPort(ports: int[]) =
  findEmptyPortUInt(ports |> Array.map(uint))

