namespace NLoop.CLI

open System
open System.CommandLine
open System.CommandLine.Invocation
open System.CommandLine.Parsing
open System.Net.Http
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open NBitcoin
open NLoop.CLI.SubCommands
open NLoop.Infrastructure.DTOs
open NLoop.Server.Services
open NLoopClient
open FSharp.Control.Tasks.Affine

module NLoopCLICommandLine =
  let private getOptions: Option seq =
    seq [
      yield! NLoop.Server.NLoopServerCommandLine.optionsForBothCliAndServer
    ]

  let getRootCommand =
    let rc = RootCommand()
    rc.Name <- "nloop"
    rc.Description <- "cli command to interact with nloopd"
    for o in getOptions do
      rc.AddGlobalOption(o)
    for v in NLoop.Server.NLoopServerCommandLine.Validators.getValidators do
      rc.AddValidator(v)
    rc.AddCommand(LoopOut.command)
    rc.AddCommand(LoopIn.command)
    rc.AddCommand(GetInfo.command)
    rc
