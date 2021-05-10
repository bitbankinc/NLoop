namespace NLoop.Domain

open System.Threading.Tasks

type Aggregate<'TState, 'TMsg, 'TEvent, 'TError> = {
  Zero: 'TState
  Apply: 'TState -> 'TEvent -> 'TState * Task<'TMsg>
  Exec: 'TState -> 'TMsg -> Task<Result<'TEvent list, 'TError>>
}
