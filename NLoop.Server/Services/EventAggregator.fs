namespace NLoop.Server.Services

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks


type IEventAggregatorSubscription =
  abstract member Unsubscribe: unit -> unit
  abstract member Resubscribe: unit -> unit

type IEventAggregator =
    abstract member Publish: 'T -> unit
    abstract member GetObservable: unit -> IObservable<'T>

type EventAggregator(logError: Action<string>) =
  member val _Subscriptions = Dictionary<Type, Dictionary<Subscription, Action<obj>>>() with get
  member this.Subscribe<'T, 'TReturn>(subsc: Func<'T, 'TReturn>) =
    this.Subscribe(Action<'T>(fun t -> subsc.Invoke(t) |> ignore))
  member this.Subscribe<'T, 'TReturn>(subsc: Func<IEventAggregatorSubscription, 'T, 'TReturn>) =
    this.Subscribe(Action<IEventAggregatorSubscription, 'T>(fun sub t -> subsc.Invoke(sub, t) |> ignore))
  member this.Subscribe<'T>(subsc: Action<'T>) =
    this.Subscribe(Action<IEventAggregatorSubscription, 'T>(fun _sub -> subsc.Invoke))
  member this.Subscribe<'T>(subsc: Func<'T, Task>) =
    let errHandler (prevTask: Task) =
      if prevTask.Status = TaskStatus.Faulted then
        Console.Error.WriteLine($"{prevTask.Exception}, Error while calling event handler")
      else
        ()
    this.Subscribe(Action<IEventAggregatorSubscription, 'T>(fun sub t ->
      subsc.Invoke(t).ContinueWith(errHandler) |> ignore)
    )
  member this.Subscribe(eventType: Type, subscription: Subscription) =
    lock (this._Subscriptions) <| fun () ->
      let mutable actions = null
      match this._Subscriptions.TryGetValue(eventType, &actions) with
      | false ->
        actions <- Dictionary<Subscription, Action<obj>>()
        this._Subscriptions.Add(eventType, actions)
      | true -> ()
      actions.Add(subscription, subscription.Act)
    subscription
  member this.Subscribe<'T>(subscription: Action<IEventAggregatorSubscription, 'T>) =
    let eventType = typeof<'T>
    let s = new Subscription(this, eventType)
    s.Act <- Action<obj>(fun o -> subscription.Invoke(s, o :?> 'T))
    this.Subscribe(eventType, s)

  member this.WaitNext<'T>(predicate: Func<'T, bool>, cancellationToken: CancellationToken) =
    let tcs = TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)
    let subsc = this.Subscribe<'T>(fun a b ->
      if predicate.Invoke(b) then
        tcs.TrySetResult(b) |> ignore
        a.Unsubscribe()
      )

    use _registration = cancellationToken.Register(fun () ->
      tcs.TrySetCanceled() |> ignore
      (subsc :> IEventAggregatorSubscription).Unsubscribe()
      ())
    tcs.Task.ConfigureAwait(false)

  member this.Publish<'T when 'T: null>(evt: 'T) =
    if (evt |> isNull) then raise <| ArgumentNullException("")

    let mutable actionList = ResizeArray<Action<obj>>()
    lock (this._Subscriptions) <| fun () ->
      match this._Subscriptions.TryGetValue(typeof<'T>) with
      | true, actions ->
        actionList <- ResizeArray actions.Values
        ()
      | _ -> ()

    for sub in actionList do
      try
        sub.Invoke(evt)
      with
      | ex ->
        logError.Invoke($"{ex}, Error while calling event handler")

  member this.WaitNext<'T>(ct: CancellationToken) =
    this.WaitNext<'T>((fun _ -> true), ct)

  interface IDisposable with
    member this.Dispose() =
      lock(this._Subscriptions) <| this._Subscriptions.Clear

and Subscription(aggregator: EventAggregator, t: Type) =
  let mutable disposed = false
  member val Act: Action<obj> = null with get, set
  interface IDisposable with
    member this.Dispose() =
      if disposed then () else
      lock (aggregator._Subscriptions) <| fun () ->
        match aggregator._Subscriptions.TryGetValue t with
        | false, _ -> ()
        | true, actions ->
          if (actions.Remove(this) && actions.Count = 0) then
            aggregator._Subscriptions.Remove(t) |> ignore
  interface IEventAggregatorSubscription with
    member this.Resubscribe() =
      aggregator.Subscribe(t, this) |> ignore
    member this.Unsubscribe() =
      (this :> IDisposable).Dispose()

