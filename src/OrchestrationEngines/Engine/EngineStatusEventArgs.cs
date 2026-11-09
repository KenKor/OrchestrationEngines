using System;

namespace OrchestrationEngines.Engine;

/// <summary>
/// Event arguments describing a change in engine lifecycle state.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EngineStatusEventArgs"/> is used to notify observers of
/// significant engine state transitions, optionally including an
/// associated error.
/// </para>
///
/// <para>
/// This type is intentionally simple and immutable, making it safe
/// to publish across threads and async boundaries.
/// </para>
/// </remarks>
/// <remarks>
/// Creates a new status event for an engine.
/// </remarks>
/// <param name="engineName">The name of the engine whose state changed.</param>
/// <param name="state">The new lifecycle state of the engine.</param>
/// <param name="error">
/// Optional error associated with the state transition, if any.
/// </param>
public sealed class EngineStatusEventArgs(
    string engineName,
    EngineState state,
    Exception error = null) : EventArgs
{
  /// <summary>
  /// Name of the engine whose state changed.
  /// </summary>
  public string EngineName { get; } = engineName;

  /// <summary>
  /// The engine's new lifecycle state.
  /// </summary>
  public EngineState State { get; } = state;

  /// <summary>
  /// Why the engine faulted: never null when <see cref="State"/> is <see cref="EngineState.Faulted"/>, and null for
  /// every other state.
  /// </summary>
  /// <remarks>
  /// For an engine whose required task failed, an <see cref="Tasks.EngineTaskNotReadyException"/> for that task (see
  /// <see cref="BaseEngine.EngineStatusChanged"/>).
  /// </remarks>
  public Exception Error { get; } = error;
}
