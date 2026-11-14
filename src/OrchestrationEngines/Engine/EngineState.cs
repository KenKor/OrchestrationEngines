using OrchestrationEngines.Mechanic;

namespace OrchestrationEngines.Engine;

/// <summary>
/// Represents the lifecycle state of an <see cref="IEngine"/>.
/// </summary>
/// <remarks>
/// <para>
/// The numeric ordering of these values is intentional and significant:
/// comparisons such as <c>State &lt; EngineState.Ready</c> are used to determine
/// whether an engine still needs to be started or completed.
/// </para>
///
/// <para>
/// States beyond <see cref="Ready"/> represent terminal or teardown states
/// from which an engine will not become Ready again without explicit reset
/// and restart by the <see cref="MechanicEngine"/>.
/// </para>
/// </remarks>
public enum EngineState
{
  /// <summary>
  /// The engine instance exists but has not been started.
  /// </summary>
  Created,

  /// <summary>
  /// The engine has begun initialization but is not yet usable.
  /// </summary>
  Starting,

  /// <summary>
  /// The engine has completed required initialization and is usable.
  /// </summary>
  Ready,

  /// <summary>
  /// The engine has been stopped and will not perform further work.
  /// </summary>
  Stopped,

  /// <summary>
  /// The engine's start failed.
  /// </summary>
  /// <remarks>
  /// Final once the engine's restart policy gives up (or when it has none): the engine then needs a reset or a new
  /// instance before it can be used again. Between the attempts of a start that the policy retries, the engine can be
  /// Faulted, and then Starting again.
  /// </remarks>
  Faulted
}
