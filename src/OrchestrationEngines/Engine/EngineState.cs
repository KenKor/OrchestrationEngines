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
  /// The engine encountered an unrecoverable fault.
  /// </summary>
  /// <remarks>
  /// Engines in this state are considered terminal and require reset/recreation
  /// before they can be used again.
  /// </remarks>
  Faulted
}
