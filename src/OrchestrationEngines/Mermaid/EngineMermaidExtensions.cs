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

    int failNoteIndex = 0;

    // 1) Emit all nodes with durations & colors
    foreach (var task in tasks.OrderBy(t => t.Name))
    {
      var id = task.Name.ForMermaid();
      var label = $"{task.Name} ({task.State}, {task.RunDuration?.ToString()})";

      sb.AppendLine($"    {id}[\"{label}\"]");

      // Style each node based on its state
      var style = GetStyleForState(id, task.State);
      if (!string.IsNullOrEmpty(style))
        styleSb.AppendLine($"    {style}");

      // Add failure reason note (if applicable)
      if (task.IsFailed && !string.IsNullOrWhiteSpace(task.FailReason))
      {
        var noteId = $"{id}_reason_{failNoteIndex++}";
        var reasonLabel = task.FailReason.Replace("\"", "\\\"");
        sb.AppendLine($"    {noteId}[\"{reasonLabel}\"]");
        sb.AppendLine($"    {id} -.-> {noteId}");
        styleSb.AppendLine($"    style {noteId} fill:#ffe5e5,stroke:#ff0000,stroke-width:1px");
      }
    }

    // 2) Emit dependency edges: parent --> child
    foreach (var child in tasks)
    {
      foreach (var parent in child.DependsOn)
      {
        var edge = $"{parent.Name.ForMermaid()} --> {child.Name.ForMermaid()}";
        if (emittedEdges.Add(edge))
          sb.AppendLine($"    {edge}");
      }
    }

    // 3) Append styles
    sb.Append(styleSb);

    sb.AppendLine("```");
    return sb.ToString();
  }

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
