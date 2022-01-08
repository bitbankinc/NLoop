namespace NLoop.Server

open System.Runtime.CompilerServices
open System
open System.Globalization
open System.Linq
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open NBitcoin

[<AbstractClass;Sealed;Extension>]
type ConfigExtensions() =
  [<Extension>]
  static member GetOrDefault<'T>(conf: IConfiguration, key: string, defaultValue: 'T) =
    let str = conf.[key] |> Option.ofObj |> function Some x -> x | None -> conf.[key.Replace(".", String.Empty)]
    if (str |> isNull) then defaultValue else

    if (typeof<'T> = typeof<bool>) then
      let trueValues = [|"1"; "true"|]
      let falseValues = [|"0"; "false"|]
      if (trueValues.Contains(str, StringComparer.OrdinalIgnoreCase)) then true |> box :?> 'T else
      if (falseValues.Contains(str, StringComparer.OrdinalIgnoreCase)) then false |> box :?> 'T else
      raise <| FormatException()
    else
      if (typeof<'T> = typeof<Uri>) then
        Uri(str, UriKind.Absolute) |> box :?> 'T
      else if (typeof<'T> = typeof<string>) then
        str |> box :?> 'T
      else if typeof<'T> = typeof<int> then
        Int32.Parse(str, CultureInfo.InvariantCulture) |> box :?> 'T
      else
        failwith $"Configuration value does not support type {typeof<'T>.Name}"


