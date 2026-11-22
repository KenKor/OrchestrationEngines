using System;

namespace OrchestrationEngines.Mechanic;

/// <summary>
/// Thrown by an engine's context task when the engine's context factory returned no context, so the task fails and is
/// recovered as any other failure.
/// </summary>
internal sealed class InvalidEngineContextException : Exception
{
  /// <summary>
  /// Creates the error for the engine named <paramref name="engineName"/>.
  /// </summary>
  /// <param name="engineName">The name of the engine whose context factory returned no context.</param>
  public InvalidEngineContextException(string engineName)
    : base($"The context factory of engine '{engineName}' returned no context.") => EngineName = engineName;

  /// <summary>
  /// The name of the engine whose context factory returned no context.
  /// </summary>
  public string EngineName { get; }
}
