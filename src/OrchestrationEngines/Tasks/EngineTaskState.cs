using System;

namespace OrchestrationEngines.Tasks;

/// <summary>
/// Set of states an Engine Task can be in
/// </summary>
[Flags]
public enum EngineTaskState : short
{
  /// <summary>
  /// Created
  /// </summary>
  Created = 0,

  /// <summary>
  /// Resetting
  /// </summary>
  Resetting = 1,

  // Everything below here is considered 'IsStarted'
  /// <summary>
  /// WaitingForDependencies
  /// </summary>
  WaitingForDependencies = 2,

  /// <summary>
  /// Running
  /// </summary>
  Running = 4,

  /// <summary>
  /// Recovering
  /// </summary>
  Recovering = 4 + 8, // Also running, but not in the Running state
                      // Everything below here is considered 'IsCompleted'
  /// <summary>
  /// CompletedWithSuccess
  /// </summary>
  CompletedWithSuccess = 16,

  /// <summary>
  /// Failed
  /// </summary>
  Failed = 32,

  /// <summary>
  /// Cancelled
  /// </summary>
  Cancelled = 32 + 64, // substate of Failed

  /// <summary>
  /// RecoveryFailed
  /// </summary>
  RecoveryFailed = 32 + 128, // substate of Failed

  /// <summary>
  /// DependencyFailed
  /// </summary>
  DependencyFailed = 32 + 256, // substate of Failed

  /// <summary>
  /// TimedOut
  /// </summary>
  TimedOut = 32 + 512, // substate of Failed
}

/// <summary>
/// Extension methods on the Engine Task State
/// </summary>
public static class EngineTaskStateExtension
{
  /// <summary>
  /// Try to set a state from a value. If value is not set, current state is returned.
  /// </summary>
  /// <param name="e"></param>
  /// <param name="value"></param>
  /// <returns></returns>
  public static EngineTaskState FromValue(this EngineTaskState e, short? value)
  {
    if (value == null || !Enum.IsDefined(typeof(EngineTaskState), value)) return e;
    return (EngineTaskState)value;
  }

  /// <summary>
  /// Try to set a state from a value. If value is not set, current state is returned.
  /// </summary>
  /// <param name="e"></param>
  /// <param name="value"></param>
  /// <returns></returns>
  public static EngineTaskState FromValue(this EngineTaskState e, int? value) => FromValue(e, (short?)value);

  /// <summary>
  /// Try to set a state from a value. If value is not set, current state is returned.
  /// </summary>
  /// <param name="e"></param>
  /// <param name="value"></param>
  /// <returns></returns>
  public static EngineTaskState FromValue(this EngineTaskState e, long? value) => FromValue(e, (short?)value);

  /// <summary>
  /// Try to set a state from a value. If value is not set, current state is returned.
  /// </summary>
  /// <param name="e"></param>
  /// <param name="value"></param>
  /// <returns></returns>
  public static EngineTaskState FromValue(this EngineTaskState e, string value) =>
      (Enum.TryParse(value, true, out EngineTaskState state)) ? state : e;

  /// <summary>
  /// Get a description for an Engine Task State
  /// </summary>
  /// <param name="e"></param>
  /// <returns></returns>
  public static string Description(this EngineTaskState e) => e switch
  {
    EngineTaskState.Created => "Task has been created, but not started.",
    EngineTaskState.WaitingForDependencies => "Task is waiting for tasks that it depends on to be completed first.",
    EngineTaskState.Running => "Task is running its normal worker.",
    EngineTaskState.Recovering => "Task is running its recovery worker.",
    EngineTaskState.CompletedWithSuccess => "Task has completed successfully.",
    EngineTaskState.Failed => "Task has failed.",
    EngineTaskState.Cancelled => "Task has been cancelled (sub state of 'Failed').",
    EngineTaskState.RecoveryFailed => "Task failed during the recovery (sub state of 'Failed').",
    EngineTaskState.DependencyFailed => "Task cannot start because its dependency failed (sub state of 'Failed').",
    EngineTaskState.TimedOut => "Task timed out (sub state of 'Timeout').",
    _ => String.Empty,
  };

  /// <summary>
  /// Get a short, human-readable display name for an Engine Task State
  /// </summary>
  /// <param name="e"></param>
  /// <returns></returns>
  public static string DisplayName(this EngineTaskState e) => e switch
  {
    EngineTaskState.Created => "Created",
    EngineTaskState.Resetting => "Resetting",
    EngineTaskState.WaitingForDependencies => "Waiting for dependencies",
    EngineTaskState.Running => "Running",
    EngineTaskState.Recovering => "Recovering",
    EngineTaskState.CompletedWithSuccess => "Completed successfully",
    EngineTaskState.Failed => "Failed",
    EngineTaskState.Cancelled => "Cancelled",
    EngineTaskState.RecoveryFailed => "Recovery failed",
    EngineTaskState.DependencyFailed => "Dependency failed",
    EngineTaskState.TimedOut => "Timed out",
    _ => e.ToString(),
  };

  /// <summary>
  /// Cast EngineTaskState to a short value
  /// </summary>
  /// <param name="e">The e.</param>
  /// <returns></returns>
  public static short AsValue(this EngineTaskState e) => (short)e;

  /// <summary>
  /// When a task has been started, i.e. it has been kicked of and moved .
  /// </summary>
  /// <param name="e">The e.</param>
  /// <returns></returns>
  public static bool IsStarted(this EngineTaskState e) => (e > EngineTaskState.Created);

  /// <summary>
  /// Returns true if the EngineTaskState means a worker (normal or recovery) is running.
  /// </summary>
  /// <param name="e">The e.</param>
  /// <returns></returns>
  public static bool IsRunning(this EngineTaskState e) => (e & EngineTaskState.Running) == EngineTaskState.Running;

  /// <summary>
  /// When a task is at its end state.
  /// </summary>
  /// <param name="e">The e.</param>
  /// <returns></returns>
  public static bool IsCompleted(this EngineTaskState e) => (e >= EngineTaskState.CompletedWithSuccess);

  /// <summary>
  /// When a task is at a failed end state.
  /// </summary>
  /// <param name="e">The e.</param>
  /// <returns></returns>
  public static bool IsFailed(this EngineTaskState e) => (e >= EngineTaskState.Failed);

}
