namespace NLoop.Domain

open System.Threading.Tasks

type Aggregate<'TState, 'TCommand, 'TEvent, 'TError, 'TDeps> = {
  Zero: 'TState
  Apply: 'TState -> 'TEvent -> 'TState
  Exec: 'TDeps -> 'TState -> 'TCommand -> Task<Result<'TEvent list, 'TError>>
}
type Aggregate<'TState, 'TCommand, 'TEvent, 'TError> = Aggregate<'TState, 'TCommand, 'TEvent, 'TError, unit>
