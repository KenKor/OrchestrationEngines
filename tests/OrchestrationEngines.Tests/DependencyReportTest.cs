using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Tests for <see cref="BaseEngine.ReportDependencies"/>: each task's dependents under it, by name, a task printed
/// once and pointed to after that, and the work linear in the number of tasks and dependencies.
/// </summary>
public class DependencyReportTest
{
  /// <summary>
  /// An engine whose tasks and dependencies are given as a list, and which counts how often its tasks are compared.
  /// </summary>
  private sealed class GraphEngine : BaseEngine
  {
    private readonly (string Name, string[] DependsOn)[] _graph;
    private readonly Dictionary<string, EngineTask> _byName = new(StringComparer.OrdinalIgnoreCase);
    private int _comparisons;

    public GraphEngine(params (string Name, string[] DependsOn)[] graph) : base(NullLogger.Instance)
    {
      _graph = graph;
      Context = new NoContext();
    }

    /// <summary>
    /// How often a task of this engine was compared with another object since the last <see cref="ResetCount"/>.
    /// </summary>
    public int Comparisons => Volatile.Read(ref _comparisons);

    public void ResetCount() => Volatile.Write(ref _comparisons, 0);

    protected override void SetUpTasks()
    {
      foreach (var (name, dependsOn) in _graph)
      {
        var task = AddTask(name, new CountedTask(name, this));
        foreach (var parent in dependsOn)
          task.AddDependsOn(_byName[parent]);
        _byName[name] = task;
      }
    }

    /// <summary>
    /// A task that counts every comparison with it: looking a task up in a list or a map compares it.
    /// </summary>
    private sealed class CountedTask(string name, GraphEngine engine)
      : EngineTask(name, _ => Task.CompletedTask, requiredForRunning: true, NullLogger.Instance,
        engine.CancellationToken)
    {
      public override bool Equals(object obj)
      {
        Interlocked.Increment(ref engine._comparisons);
        return base.Equals(obj);
      }

      public override int GetHashCode() => base.GetHashCode();
    }
  }

  private static (string, string[]) T(string name, params string[] dependsOn) => (name, dependsOn);

  private static async Task<GraphEngine> ReadyAsync(GraphEngine engine)
  {
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    engine.State.Should().Be(EngineState.Ready);
    return engine;
  }

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
    engine.ResetCount();

    engine.ReportDependencies();

    engine.Comparisons.Should().BeLessThan(4 * (dependents + 1),
      "each task and each dependency is looked at a fixed number of times");
  }
}
