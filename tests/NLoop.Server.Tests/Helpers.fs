module Helpers

open System.Text
open NBitcoin.DataEncoders
open System.IO
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open BTCPayServer.Lightning
open NBitcoin
open NLoop.Server.Services

let getLocalBoltzClient() =
  let b = BoltzClient("http://localhost", 9001, Network.RegTest)
  b

let private GetCertFingerPrint(filePath: string) =
  use cert = new X509Certificate2(filePath)
  use hashAlg = SHA256.Create()
  hashAlg.ComputeHash(cert.RawData)

let hex = HexEncoder()
let private getCertFingerPrintHex (filePath: string) =
  GetCertFingerPrint filePath |> hex.EncodeData
let factory = LightningClientFactory(Network.RegTest)

let getUserLndClient() =
  let dataPath = Path.GetFullPath(Path.Join(Directory.GetCurrentDirectory(), "..", "..", "..", "data"))
  let lndMacaroonPath = Path.Join(dataPath, ".lnd_user", "chain", "bitcoin", "regtest", "admin.macaroon")
  let lndTlsCertThumbPrint = getCertFingerPrintHex(Path.Join(dataPath, ".lnd_user", "tls.cert"))
  factory.Create($"type=lnd-rest;macaroonfilepath={lndMacaroonPath};certthumbprint={lndTlsCertThumbPrint};server=https://localhost:32736")
