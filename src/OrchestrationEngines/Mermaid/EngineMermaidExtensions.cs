using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Mermaid;

/// <summary>
/// Renders an engine's tasks as a Mermaid diagram.
/// </summary>
public static class EngineMermaidExtensions
{
  /// <summary>
  /// The words of a Mermaid flowchart that cannot be a node id. Mermaid matches them case-sensitively, as a whole id.
  /// </summary>
  internal static readonly string[] Keywords =
  [
    "end", "graph", "flowchart", "subgraph", "direction", "style", "linkStyle", "classDef", "class", "click", "call",
    "href", "interpolate", "default",
  ];

  /// <summary>
  /// Produces a Mermaid diagram representing the engine's task dependencies and states.
  /// </summary>
  /// <param name="engine">The engine whose tasks are drawn.</param>
  /// <returns>
  /// A fenced Mermaid code block: a node per task (with its state and run duration, coloured by state), a note with
  /// the reason for each failed task, and an edge from each task to the tasks that depend on it.
  /// </returns>
  public static string ReportDependenciesMermaid(this BaseEngine engine)
  {
    ArgumentNullException.ThrowIfNull(engine);

    var tasks = engine.Tasks.Values;
    var sb = new StringBuilder();
    sb.AppendLine("```mermaid");
    sb.AppendLine("graph TD");

    // Prevent duplicate edges
    var emittedEdges = new HashSet<string>(StringComparer.Ordinal);

    // Collect style lines separately
    var styleSb = new StringBuilder();

    // Every node gets its own id: names that differ only in characters an id cannot hold ("A-B", "A B") would
    // otherwise share one. Mermaid's keywords are taken from the start, as a node id that is one does not parse.
    var usedIds = new HashSet<string>(Keywords, StringComparer.Ordinal);
    // By name, as task names are unique ignoring case: a dependency held from before a task was replaced points at
    // the node of the task with its name.
    var idByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string IdOf(string name)
    {
      if (!idByName.TryGetValue(name, out var id))
        idByName[name] = id = UniqueId(name.ForMermaid(), usedIds);
      return id;
    }

    // 1) Emit all nodes with durations & colors
    foreach (var task in tasks.OrderBy(t => t.Name))
    {
      var id = IdOf(task.Name);
      var label = ForLabel($"{task.Name} ({task.State}, {task.RunDuration?.ToString()})");

      sb.AppendLine($"    {id}[\"{label}\"]");

      // Style each node based on its state
      var style = GetStyleForState(id, task.State);
      styleSb.AppendLine($"    {style}");

      // Add failure reason note (if applicable)
      if (task.IsFailed && !string.IsNullOrWhiteSpace(task.FailReason))
      {
        var noteId = UniqueId($"{id}_reason", usedIds);
        sb.AppendLine($"    {noteId}[\"{ForLabel(task.FailReason)}\"]");
        sb.AppendLine($"    {id} -.-> {noteId}");
        styleSb.AppendLine($"    style {noteId} fill:#ffe5e5,stroke:#ff0000,stroke-width:1px");
      }
    }

    // 2) Emit dependency edges: parent --> child
    foreach (var child in tasks)
    {
      foreach (var parent in child.DependsOn)
      {
        var edge = $"{IdOf(parent.Name)} --> {IdOf(child.Name)}";
        if (emittedEdges.Add(edge))
          sb.AppendLine($"    {edge}");
      }
    }

    // 3) Append styles
    sb.Append(styleSb);

    sb.AppendLine("```");
    return sb.ToString();
  }

  /// <summary>
  /// Returns <paramref name="id"/>, or the first of "<paramref name="id"/>_2", "_3", ... that is not used yet, and
  /// marks it used.
  /// </summary>
  private static string UniqueId(string id, HashSet<string> usedIds)
  {
    var unique = id;
    for (var n = 2; !usedIds.Add(unique); n++)
      unique = $"{id}_{n}";
    return unique;
  }

  /// <summary>
  /// Writes <paramref name="text"/> for a quoted Mermaid label: '#', quotes, '&lt;' and '&gt;' as Mermaid entity
  /// codes, so they show as text, and a line break as a space, so the node stays on one line.
  /// </summary>
  private static string ForLabel(string text) => text
    .Replace("#", "#35;")
    .Replace("\"", "#quot;")
    .Replace("<", "#lt;")
    .Replace(">", "#gt;")
    .Replace("\r\n", " ")
    .Replace('\r', ' ')
    .Replace('\n', ' ');

  private static string GetStyleForState(string id, EngineTaskState state)
  {
    var fill = state switch
    {
      EngineTaskState.CompletedWithSuccess => "#c9f7c9", // green
      EngineTaskState.Running => "#c9e7ff", // blue
      EngineTaskState.Recovering => "#ffd59e", // orange
      EngineTaskState.WaitingForDependencies => "#eeeeee", // gray
      EngineTaskState.Created => "#ffffff", // white
      EngineTaskState.Failed => "#ffc9c9", // red
      EngineTaskState.Cancelled => "#f5b5b5", // muted red
      _ => "#ffffff"
    };
    return $"style {id} fill:{fill},stroke:#333333,stroke-width:1px";
  }
}
