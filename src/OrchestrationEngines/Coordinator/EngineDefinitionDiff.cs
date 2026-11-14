using System;
using System.Collections.Generic;
using System.Linq;

namespace OrchestrationEngines.Coordinator;

/// <summary>
/// Version-focused diff between currently active and candidate engine definitions.
/// </summary>
/// <remarks>
/// This diff intentionally keeps decision logic simple:
/// - restart roots are engines whose definition version changed,
/// - restart closure is those roots plus all transitive dependents.
/// </remarks>
public sealed class EngineDefinitionDiff
{
  private EngineDefinitionDiff(
    IReadOnlyCollection<string> addedEngineNames,
    IReadOnlyCollection<string> removedEngineNames,
    IReadOnlyCollection<string> versionChangedEngineNames,
    IReadOnlyCollection<string> restartRequiredEngineNames)
  {
    AddedEngineNames = addedEngineNames;
    RemovedEngineNames = removedEngineNames;
    VersionChangedEngineNames = versionChangedEngineNames;
    RestartRequiredEngineNames = restartRequiredEngineNames;
  }

  /// <summary>
  /// Engine names present in candidate definitions, but not in current definitions.
  /// </summary>
  public IReadOnlyCollection<string> AddedEngineNames { get; }

  /// <summary>
  /// Engine names present in current definitions, but not in candidate definitions.
  /// </summary>
  public IReadOnlyCollection<string> RemovedEngineNames { get; }

  /// <summary>
  /// Engine names that exist in both sets but have a different integer version.
  /// </summary>
  public IReadOnlyCollection<string> VersionChangedEngineNames { get; }

  /// <summary>
  /// Engines that should restart:
  /// version-changed engines plus all transitive dependents.
  /// </summary>
  public IReadOnlyCollection<string> RestartRequiredEngineNames { get; }

  /// <summary>
  /// Returns true when at least one engine version changed.
  /// </summary>
  public bool HasVersionChanges => VersionChangedEngineNames.Count > 0;

  /// <summary>
  /// Returns true when any restart is required.
  /// </summary>
  public bool RequiresRestart => RestartRequiredEngineNames.Count > 0;

  /// <summary>
  /// Builds a diff between current and candidate definitions.
  /// </summary>
  public static EngineDefinitionDiff Create(
    IEnumerable<EngineDescriptor> currentDescriptors,
    IEnumerable<EngineDescriptor> candidateDescriptors)
  {
    var currentByName = ToDictionaryByName(currentDescriptors, nameof(currentDescriptors));
    var candidateByName = ToDictionaryByName(candidateDescriptors, nameof(candidateDescriptors));

    var added = candidateByName.Keys
      .Except(currentByName.Keys, StringComparer.OrdinalIgnoreCase)
      .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
      .ToArray();

    var removed = currentByName.Keys
      .Except(candidateByName.Keys, StringComparer.OrdinalIgnoreCase)
      .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
      .ToArray();

    var versionChanged = candidateByName.Keys
      .Where(name =>
        currentByName.TryGetValue(name, out var current) &&
        candidateByName.TryGetValue(name, out var candidate) &&
        current.Version != candidate.Version)
      .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
      .ToArray();

    var restartRequired = ComputeRestartClosure(versionChanged, candidateByName);

    return new EngineDefinitionDiff(added, removed, versionChanged, restartRequired);
  }

  private static Dictionary<string, EngineDescriptor> ToDictionaryByName(
    IEnumerable<EngineDescriptor> descriptors,
    string paramName)
  {
    var byName = new Dictionary<string, EngineDescriptor>(StringComparer.OrdinalIgnoreCase);
    if (descriptors == null)
      return byName;

    foreach (var descriptor in descriptors)
    {
      if (descriptor == null)
        continue;

      if (byName.ContainsKey(descriptor.Name))
        throw new ArgumentException(
          $"Duplicate engine descriptor name '{descriptor.Name}'.",
          paramName);

      byName.Add(descriptor.Name, descriptor);
    }

    return byName;
  }

  private static IReadOnlyCollection<string> ComputeRestartClosure(
    IReadOnlyCollection<string> versionChangedEngineNames,
    IReadOnlyDictionary<string, EngineDescriptor> candidateByName)
  {
    if (versionChangedEngineNames.Count == 0)
      return Array.Empty<string>();

    var dependentsByEngine = EngineDependents.Of(candidateByName.Values, candidateByName.ContainsKey);

    var restartSet = new HashSet<string>(versionChangedEngineNames, StringComparer.OrdinalIgnoreCase);
    var queue = new Queue<string>(restartSet);

    while (queue.Count > 0)
    {
      var current = queue.Dequeue();
      if (!dependentsByEngine.TryGetValue(current, out var dependents))
        continue;

      foreach (var dependent in dependents)
      {
        if (!restartSet.Add(dependent))
          continue;

        queue.Enqueue(dependent);
      }
    }

    return restartSet
      .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
      .ToArray();
  }
}
