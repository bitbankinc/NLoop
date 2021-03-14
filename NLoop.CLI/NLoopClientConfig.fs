namespace NLoop.CLI

open System


type NLoopClientConfig = {
  AllowInsecure: bool
  Uri: Uri
  CertificateThumbPrint: byte[] option
}
