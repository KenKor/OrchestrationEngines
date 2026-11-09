using System;

namespace OrchestrationEngines.Tasks;

/// <summary>
/// Thrown when a task that was waited for did not complete with success: it ended Failed (or TimedOut,
/// DependencyFailed, RecoveryFailed) or Cancelled.
/// </summary>
/// <remarks>
/// It carries the task's name, the state it ended in and its <see cref="EngineTask.FailReason"/>. When the work or the
/// recovery threw, that exception is the <see cref="Exception.InnerException"/>; for a time-out, a cancellation or a
/// failed dependency there is none. It does not derive from <see cref="EngineTaskException"/>: a task's work that
/// waits for another task and lets this propagate fails, rather than being cancelled.
/// </remarks>
public sealed class EngineTaskNotReadyException : Exception
{
  /// <summary>
  /// Creates the exception for a task that ended in <paramref name="state"/>.
  /// </summary>
  /// <param name="taskName">The name of the task.</param>
  /// <param name="state">The state the task ended in.</param>
  /// <param name="failReason">Why it ended so, if known.</param>
  /// <param name="innerException">The exception its work or recovery threw, if any.</param>
  public EngineTaskNotReadyException(string taskName, EngineTaskState state, string failReason,
    Exception innerException = null)
    : base(string.IsNullOrEmpty(failReason)
      ? $"Task '{taskName}' did not complete: it ended {state}."
      : $"Task '{taskName}' did not complete: it ended {state}: {failReason}", innerException)
  {
    TaskName = taskName;
    State = state;
    FailReason = failReason;
  }

  /// <summary>
  /// The name of the task.
  /// </summary>
  public string TaskName { get; }

  /// <summary>
  /// The state the task ended in.
  /// </summary>
  public EngineTaskState State { get; }

  /// <summary>
  /// Why the task ended so (its <see cref="EngineTask.FailReason"/>), if known.
  /// </summary>
  public string FailReason { get; }

  /// <summary>
  /// Creates the exception for <paramref name="task"/>, which ended in <paramref name="state"/>. The exception its work
  /// or recovery threw is passed on only for a task that failed by itself (Failed or RecoveryFailed).
  /// </summary>
  internal static EngineTaskNotReadyException For(EngineTask task, EngineTaskState state) =>
    new(task.Name, state, task.FailReason,
      state is EngineTaskState.Failed or EngineTaskState.RecoveryFailed ? task.FailException : null);
}
