namespace NLoop.Domain

open System.Threading.Tasks

type Aggregate<'TState, 'TMsg, 'TEvent> = {
  Zero: 'TState
  Apply: 'TState -> 'TEvent -> 'TState * Cmd<'TMsg>
  Exec: 'TState -> 'TMsg -> Task<'TEvent list>
}

type AsyncOperationStatus<'T> =
  | Started
  | Finished of 'T

