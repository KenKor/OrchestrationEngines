namespace OrchestrationEngines.Coordinator;

/// <summary>
/// Triggers that move the Engine Coordinator through its states.
/// </summary>
public enum EngineCoordinatorTrigger
{
  /// <summary>
  /// App start requested
  /// </summary>
  Start,

  /// <summary>
  /// Some engine started
  /// </summary>
  EngineStarting,

  /// <summary>
  /// All engines ready
  /// </summary>
  EngineReady,

  /// <summary>
  /// An engine failed
  /// </summary>
  EngineFailed,

  /// <summary>
  /// Engine refresh requested
  /// Meaning: a specific engine should be restarted or reinitialized (mechanic performs the actual action).
  /// </summary>
  RefreshEngine,

  /// <summary>
  /// Begin shutdown
  /// </summary>
  StartShutdown,

  /// <summary>
  /// Finished shutdown
  /// </summary>
  CompleteShutdown
}
