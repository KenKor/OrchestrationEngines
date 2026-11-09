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
  /// Optional exception associated with the state change.
  /// </summary>
  /// <remarks>
  /// This is typically populated when the engine transitions
  /// into a faulted or terminal state.
  /// </remarks>
  public Exception Error { get; } = error;
}
