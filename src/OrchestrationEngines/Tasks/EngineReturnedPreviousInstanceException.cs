namespace OrchestrationEngines.Tasks;

/// <summary>
/// Thrown when a generation switch's engine factory (see <c>EngineDescriptor.SetRestartFactory</c>) returns the
/// previous engine instead of a new one: a switch must produce a fresh instance with a fresh token chain.
/// </summary>
/// <remarks>
/// Like every <see cref="EngineTaskException"/>, it is not retried: the factory would return the same instance again.
/// It is thrown by the factory the switch hands to the engine's creation task, which cannot reach that task, so
/// <see cref="EngineTaskException.EngineTask"/> is null.
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
