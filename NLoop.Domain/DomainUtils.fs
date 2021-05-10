namespace NLoop.Domain

open System.Threading.Tasks

type Aggregate<'TState, 'TMsg, 'TEvent, 'TError> = {
  Zero: 'TState
  Apply: 'TState -> 'TEvent -> 'TState * 'TMsg option
  Exec: 'TState -> 'TMsg -> Task<Result<'TEvent list, 'TError>>
}

