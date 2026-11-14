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
  internal static Dictionary<string, HashSet<string>> Of(IEnumerable<EngineDescriptor> descriptors,
    Func<string, bool> includes)
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
        dependents.Add(descriptor.Name);
      }
    }
    return dependentsByEngine;
  }
}
