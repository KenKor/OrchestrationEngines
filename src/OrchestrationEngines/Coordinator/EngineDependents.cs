using System;
using System.Collections.Generic;

namespace OrchestrationEngines.Coordinator;

/// <summary>
/// The reverse of the descriptors' dependencies: for each engine, the engines that depend on it.
/// </summary>
internal static class EngineDependents
{
  /// <summary>
  /// For each engine that <paramref name="includes"/>, the included engines whose descriptors depend on it, by name
  /// (case-insensitive). An engine that no included descriptor depends on has no entry; null names are skipped.
  /// </summary>
  /// <param name="descriptors">The descriptors whose <see cref="EngineDescriptor.DependsOn"/> are reversed.</param>
  /// <param name="includes">Whether an engine, by name, is part of the graph.</param>
  /// <param name="dependencies">
  /// If given, receives the same edges the other way round: for each included engine, the included engines it depends
  /// on, each once.
  /// </param>
  internal static Dictionary<string, HashSet<string>> Of(IEnumerable<EngineDescriptor> descriptors,
    Func<string, bool> includes, Dictionary<string, List<string>> dependencies = null)
  {
    var dependentsByEngine = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    foreach (var descriptor in descriptors)
    {
      if (descriptor.Name == null || !includes(descriptor.Name))
        continue;
      foreach (var dependency in descriptor.DependsOn)
      {
        if (dependency == null || !includes(dependency))
          continue;
        if (!dependentsByEngine.TryGetValue(dependency, out var dependents))
        {
          dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
          dependentsByEngine[dependency] = dependents;
        }
        if (!dependents.Add(descriptor.Name) || dependencies == null)
          continue;
        if (!dependencies.TryGetValue(descriptor.Name, out var dependenciesOfEngine))
        {
          dependenciesOfEngine = [];
          dependencies[descriptor.Name] = dependenciesOfEngine;
        }
        dependenciesOfEngine.Add(dependency);
      }
    }
    return dependentsByEngine;
  }
}
