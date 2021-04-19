namespace NLoop.Domain

open System.Threading.Tasks

type Aggregate<'TState, 'TCommand, 'TEvent, 'TError> = {
  Zero: 'TState
  Apply: 'TState -> 'TEvent -> 'TState
  Exec: 'TState -> 'TCommand -> Task<Result<'TEvent list, 'TError>>
}
