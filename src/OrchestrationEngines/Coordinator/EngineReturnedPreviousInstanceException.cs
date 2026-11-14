using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Coordinator;

/// <summary>
/// Thrown when a generation switch's engine factory (see <see cref="EngineDescriptor.SetRestartFactory"/>) returns the
/// previous engine instead of a new one: a switch must produce a fresh instance with a fresh token chain.
/// </summary>
/// <remarks>
/// Like every <see cref="EngineTaskException"/>, it is not retried by the task that throws it, here the engine's
/// creation task: the factory would return the same instance again. It is thrown by the factory the switch hands to
/// that task, which cannot reach the task, so <see cref="EngineTaskException.EngineTask"/> is null.
/// </remarks>
public sealed class EngineReturnedPreviousInstanceException : EngineTaskException
{
  /// <summary>
  /// Creates the exception for an engine.
  /// </summary>
  /// <param name="engineName">The name of the engine whose factory returned the previous instance.</param>
  public EngineReturnedPreviousInstanceException(string engineName)
    : base(null, $"Engine '{engineName}' returned the previous instance during generation switch.")
  {
    EngineName = engineName;
  }

  /// <summary>
  /// The name of the engine whose factory returned the previous instance.
  /// </summary>
  public string EngineName { get; }
}
