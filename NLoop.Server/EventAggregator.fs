namespace NLoop.Server

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks

open System.Reactive
open FSharp.Control.Reactive
open System.Reactive.Linq
open System.Reactive.Subjects
open Microsoft.Extensions.Logging
open NLoop.Server.DTOs
type IEventAggregator =
  abstract member Publish: 'T -> unit
  abstract member GetObservable: unit -> IObservable<'T>

[<Sealed;AbstractClass;Extension>]
type EventAggregatorExtensions() =
  [<Extension>]
  static member GetObservable<'T, 'U>(this: IEventAggregator): IObservable<Choice<'T, 'U>> =
    Observable.merge
      (this.GetObservable<'T>() |> Observable.map(box))
      (this.GetObservable<'U>() |> Observable.map(box))
    |> Observable.map(function
        | :? 'T as t -> Choice1Of2 t
        | :? 'U as u -> Choice2Of2 u
        | _ -> failwith "unreachable"
    )

type ReactiveEventAggregator() =
  let _subject = new Subject<obj>()

  let mutable disposed = false

  interface IEventAggregator with
    member this.Publish<'T>(item: 'T) =
      _subject.OnNext(item)
    member this.GetObservable<'T>() =
      _subject.OfType<'T>().AsObservable()

  interface IDisposable with
    member this.Dispose() =
      if (disposed) then () else
      _subject.Dispose()
      disposed <- true
