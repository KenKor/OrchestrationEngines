using System;
using OrchestrationEngines.Engine;

namespace OrchestrationEngines.Coordinator;

/// <summary>
/// Input payload for warm-restart engine creation.
/// </summary>
/// <remarks>
/// During a generation switch, restart factories can inspect the previous runtime
/// engine and optionally transfer reusable state into the new instance.
/// </remarks>
/// <remarks>
/// Creates a restart input instance.
/// </remarks>
public sealed class EngineRestartInput(
  string name,
  EngineDescriptor newDescriptor,
  IEngine previousEngine,
  EngineDescriptor previousDescriptor = null,
  bool definitionChanged = false,
  bool dependencyChanged = false)
{
  /// <summary>
  /// Logical engine name.
  /// </summary>
  public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

  /// <summary>
  /// Descriptor for the new generation.
  /// </summary>
  public EngineDescriptor NewDescriptor { get; } = newDescriptor ?? throw new ArgumentNullException(nameof(newDescriptor));

  /// <summary>
  /// Previously running engine instance.
  /// </summary>
  public IEngine PreviousEngine { get; } = previousEngine ?? throw new ArgumentNullException(nameof(previousEngine));

  /// <summary>
  /// Descriptor used by the previous generation, if known.
  /// </summary>
  public EngineDescriptor PreviousDescriptor { get; } = previousDescriptor;

  /// <summary>
  /// True when this engine's own descriptor version changed between generations.
  /// </summary>
  public bool DefinitionChanged { get; } = definitionChanged;

  /// <summary>
  /// True when this engine is restarted because an upstream dependency changed.
  /// </summary>
  public bool DependencyChanged { get; } = dependencyChanged;

  /// <summary>
  /// True when this engine is being handed over without definition or dependency change.
  /// </summary>
  public bool NothingChanged => !DefinitionChanged && !DependencyChanged;
}
