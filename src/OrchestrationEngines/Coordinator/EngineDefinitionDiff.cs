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
internal sealed class EngineDefinitionDiff
{
  private EngineDefinitionDiff(
    IReadOnlyCollection<string> versionChangedEngineNames,
    IReadOnlyCollection<string> restartRequiredEngineNames)
  {
    VersionChangedEngineNames = versionChangedEngineNames;
    RestartRequiredEngineNames = restartRequiredEngineNames;
  }

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
  /// Builds a diff between definitions already mapped by name (see <see cref="ToDictionaryByName"/>).
  /// </summary>
  internal static EngineDefinitionDiff Create(
    IReadOnlyDictionary<string, EngineDescriptor> currentByName,
    IReadOnlyDictionary<string, EngineDescriptor> candidateByName)
  {
    var versionChanged = candidateByName
      .Where(pair => currentByName.TryGetValue(pair.Key, out var current) && current.Version != pair.Value.Version)
      .Select(pair => pair.Key)
      .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
      .ToArray();

    var restartRequired = ComputeRestartClosure(versionChanged, candidateByName);

    return new EngineDefinitionDiff(versionChanged, restartRequired);
  }

  /// <summary>
  /// Maps descriptors by name, ignoring case.
  /// </summary>
  /// <exception cref="ArgumentException">Two descriptors have the same name.</exception>
  internal static Dictionary<string, EngineDescriptor> ToDictionaryByName(
    IEnumerable<EngineDescriptor> descriptors,
    string paramName)
  {
    var byName = new Dictionary<string, EngineDescriptor>(StringComparer.OrdinalIgnoreCase);
    foreach (var descriptor in descriptors)
    {
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
