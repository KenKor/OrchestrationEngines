# Engine Coordinator state

The coordinator's state (`EngineCoordinator.State`) is not stored: every read computes it, from the coordinator's own
start and shutdown and from the engines registered as required for running, as an engine's state is computed from its
tasks. So it cannot differ from what it describes. The first row that applies gives the state:

| State | When |
|---|---|
| `Stopped` | The shutdown has completed. |
| `Stopping` | The shutdown has started. |
| `Created` | The coordinator has not been started. |
| `WaitingForEngines` | A required engine is `Faulted` or `Stopped`, and is not being started again. |
| `Initializing` | A context operation (an invalidation or a refresh) is in flight. |
| `Running` | Every required engine is `Ready`. |
| `Initializing` | A required engine has begun to start. |
| `WaitingForEngines` | Started, but no required engine has begun to start yet. |

- A `Faulted` or `Stopped` required engine comes before a `Starting` one: with one engine failed and another still
  loading, the coordinator is `WaitingForEngines`.
- An engine that is `Faulted` or `Stopped` counts as starting while its kick-start has not given up: its restart
  policy starts it again, and the coordinator stays `Initializing` meanwhile, as a lookup waits meanwhile.
- An engine that has not started (not created yet, or still `Created`) has no state of its own to go by. Its
  kick-start in the mechanic stands in: the engine counts as `Faulted` once the kick-start has given up (the engine
  could not be created, its context failed, or an engine it depends on failed), and as starting once it exists or its
  kick-start has been fired.
- An engine registered as not required for running does not count.

A change of the state is logged (debug, `Coordinator transition <old> --> <new>`) when it is seen: on a read of
`State`, or on one of the events that can cause it (the start, the shutdown, a context operation, a change of the
mechanic's state).

A generation switch (`RestartWithDefinitionsAsync`) shuts the replaced coordinator down, so a replaced coordinator ends
in `Stopped`; lookups on it are answered by the current coordinator of its lineage (`Successor`). A shutdown reaches
`Stopped` also when an engine fails to stop, and also on a coordinator that never started: that one never starts an
engine afterwards.

`CoordinatorComputedStateTest` (in `tests/OrchestrationEngines.Tests`) holds a table of these rules.
`StateMachineTransitionsTest` holds a snapshot of every transition of the task state machine (`EngineTask`), so a
change to it shows up in review.
