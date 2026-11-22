using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using OrchestrationEngines.Engine;
using Xunit;
using static OrchestrationEngines.Tests.GraphEngine;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Tests for <see cref="BaseEngine.ReportDependencies"/>: each task's dependents under it, by name, a task printed
/// once and pointed to after that, and the work linear in the number of tasks and dependencies.
/// </summary>
public class DependencyReportTest
{
  /// <summary>
  /// Verifies the report: roots by name, each task's dependents under it by name, a task reached a second time as a
  /// pointer, and a dependency added twice as one.
  /// </summary>
  [Fact]
  public async Task ReportDependencies_Graph_ListsEachTaskOnceUnderItsDependencies()
  {
    var engine = await ReadyAsync(new GraphEngine(
      T("Root2"),
      T("Root1"),
      T("C", "Root1"),
      T("B", "Root1"),
      T("D", "C", "B"),
      T("E", "Root2", "Root2")));

    var lines = engine.ReportDependencies().Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);

    lines.Should().Equal(
      engine.ToString(),
      "Root1 [CompletedWithSuccess]",
      " -> B [CompletedWithSuccess]",
      "    -> D [CompletedWithSuccess]",
      " -> C [CompletedWithSuccess]",
      "    -> D -> ...",
      "Root2 [CompletedWithSuccess]",
      " -> E [CompletedWithSuccess]");
  }

  /// <summary>
  /// Verifies that the report finds each task's dependents once, not by looking through every task for each task:
  /// one root with many dependents compares tasks a number of times in the order of the tasks, not of their square.
  /// </summary>
  [Fact]
  public async Task ReportDependencies_ManyDependents_ComparesTasksLinearly()
  {
    const int dependents = 200;
    var graph = new List<(string, string[])> { T("Root") };
    for (var i = 0; i < dependents; i++)
      graph.Add(T($"Task{i:D3}", "Root"));
    var engine = await ReadyAsync(new GraphEngine([.. graph]));
    var comparisons = 0;
    engine.OnCompare = () => Interlocked.Increment(ref comparisons);

    engine.ReportDependencies();

    Volatile.Read(ref comparisons).Should().BeLessThan(4 * (dependents + 1),
      "each task and each dependency is looked at a fixed number of times");
  }
}
