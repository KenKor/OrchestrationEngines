namespace OrchestrationEngines.Coordinator;

/// <summary>
/// An engine that, created in a generation switch, holds on to the engine it replaces until it starts (for example to
/// take over its loaded state).
/// </summary>
/// <remarks>
/// An engine that never starts (its dependency failed, its creation or context kept failing) would hold the previous
/// engine, and everything that engine loaded, for as long as its own generation lives. The coordinator therefore lets
/// it go once the engine's kick-start has settled: the engine started, or its kick-start gave up.
/// </remarks>
public interface IReplacesPreviousEngine
{
  /// <summary>
  /// Lets go of the previous engine, if this engine still holds it.
  /// </summary>
  void ReleasePreviousEngine();
}
