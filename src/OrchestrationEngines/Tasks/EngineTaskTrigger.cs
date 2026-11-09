namespace OrchestrationEngines.Tasks;

/// <summary>
/// Defines the triggers that drive state transitions in the <see cref="EngineTask"/> lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EngineTaskTrigger"/> values are emitted by task execution, dependency tracking,
/// recovery logic, and external control paths. Each trigger represents a discrete event
/// that may cause a state transition in the engine task state machine.
/// </para>
/// <para>
/// The triggers are intentionally fine-grained to allow precise modeling of task execution,
/// failure, and recovery behavior.
/// </para>
/// </remarks>
public enum EngineTaskTrigger
{
  /// <summary>
  /// Signals that the task should begin execution.
  /// </summary>
  /// <remarks>
  /// Typically fired when the task is first scheduled or explicitly started.
  /// </remarks>
  Start,

  /// <summary>
  /// Signals that the task should reset to its initial state.
  /// </summary>
  /// <remarks>
  /// Used to clear previous execution or failure state so the task can be started again
  /// as a fresh run.
  /// </remarks>
  Reset,

  /// <summary>
  /// Indicates that a required dependency has completed successfully.
  /// </summary>
  /// <remarks>
  /// This trigger is typically fired once per dependency and contributes to unblocking
  /// task execution when all dependencies have succeeded.
  /// </remarks>
  CompleteDependency,

  /// <summary>
  /// Indicates that a required dependency has failed.
  /// </summary>
  /// <remarks>
  /// Dependency failure generally prevents task execution and may transition the task
  /// into a failed or blocked state.
  /// </remarks>
  FailDependency,

  /// <summary>
  /// Signals that the task has completed its work successfully.
  /// </summary>
  Succeed,

  /// <summary>
  /// Signals that the task has failed during execution.
  /// </summary>
  /// <remarks>
  /// This trigger is typically followed by a transition into recovery or terminal failure,
  /// depending on the configured restart policy.
  /// </remarks>
  Fail,

  /// <summary>
  /// Signals that the task has exceeded its allowed execution time.
  /// </summary>
  /// <remarks>
  /// Timeout handling is treated as a distinct failure mode to allow specialized
  /// recovery or diagnostics.
  /// </remarks>
  TimeOut,

  /// <summary>
  /// Signals that the task has been cancelled.
  /// </summary>
  /// <remarks>
  /// Cancellation may originate from external control flow or engine shutdown and is
  /// considered distinct from failure.
  /// </remarks>
  Cancel,

  /// <summary>
  /// Signals that a recovery attempt has completed successfully.
  /// </summary>
  /// <remarks>
  /// This trigger typically causes the task to transition back into a runnable or
  /// restarting state.
  /// </remarks>
  SucceedRecovery,

  /// <summary>
  /// Signals that a recovery attempt has failed.
  /// </summary>
  /// <remarks>
  /// Recovery failure may result in another recovery attempt or a terminal failure,
  /// depending on the restart policy.
  /// </remarks>
  FailRecovery,
}
