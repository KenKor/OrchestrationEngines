using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// An engine whose tasks and dependencies are given as a list, and which counts each task's runs. Shared by the
/// tests of task resets and of the dependency report.
/// </summary>
internal sealed class GraphEngine : BaseEngine
{
  private readonly (string Name, string[] DependsOn)[] _graph;
  private readonly ConcurrentDictionary<string, int> _runs = new(StringComparer.OrdinalIgnoreCase);
  private readonly ConcurrentDictionary<string, int> _resets = new(StringComparer.OrdinalIgnoreCase);

  public GraphEngine(params (string Name, string[] DependsOn)[] graph) : this(NullLogger.Instance, graph)
  {
  }

  public GraphEngine(ILogger logger, params (string Name, string[] DependsOn)[] graph) : base(logger)
  {
    _graph = graph;
    Context = new NoContext();
  }

  /// <summary>
  /// Runs after the count, with the task's name, its run number and its token.
  /// </summary>
  public Func<string, int, CancellationToken, Task> Work { get; init; }

  /// <summary>
  /// Runs as a task's reset begins (before it is back in Created), with the task's name.
  /// </summary>
  public Action<string> OnReset { get; set; }

  /// <summary>
  /// Runs once the base reset handler has run (the task has its new token and completion latch), with the task's name.
  /// </summary>
  public Action<string> AfterReset { get; set; }

  /// <summary>
  /// Runs as a task with dependencies looks whether they have all completed, with the task's name.
  /// </summary>
  public Action<string> OnDependencyCheck { get; set; }

  /// <summary>
  /// Runs as anything looks whether a task has started, with the task's name.
  /// </summary>
  public Action<string> OnStartedLook { get; set; }

  /// <summary>
  /// Runs each time a task is compared with another object: looking a task up in a list or a map compares it.
  /// </summary>
  public Action OnCompare { get; set; }

  public int Runs(string name) => _runs.TryGetValue(name, out var runs) ? runs : 0;

  /// <summary>
  /// How often the task's reset has begun.
  /// </summary>
  public int Resets(string name) => _resets.TryGetValue(name, out var resets) ? resets : 0;

  protected override void SetUpTasks()
  {
    foreach (var (name, dependsOn) in _graph)
      Add(name, dependsOn);
  }

  /// <summary>
  /// Adds a task; after the set-up, to a set the engine's cycle check has already seen.
  /// </summary>
  public EngineTask Add(string name, params string[] dependsOn)
  {
    var task = AddTask(name, new HookedTask(name, token =>
    {
      var run = _runs.AddOrUpdate(name, 1, (_, n) => n + 1);
      return Work?.Invoke(name, run, token) ?? Task.CompletedTask;
    }, this));
    foreach (var parent in dependsOn)
      task.AddDependsOn(_byName[parent]);
    _byName[name] = task;
    return task;
  }

  // The latest task of each name: during the set-up, the set is not published yet.
  private readonly Dictionary<string, EngineTask> _byName = new(StringComparer.OrdinalIgnoreCase);

  private sealed class HookedTask(string name, Func<CancellationToken, Task> work, GraphEngine engine)
    : EngineTask(name, work, requiredForRunning: true, NullLogger.Instance, engine.CancellationToken)
  {
    private protected override async Task OnResetEnteredAsync()
    {
      engine._resets.AddOrUpdate(Name, 1, (_, n) => n + 1);
      engine.OnReset?.Invoke(Name);
      await base.OnResetEnteredAsync();
      engine.AfterReset?.Invoke(Name);
    }

    private protected override bool DependenciesReady()
    {
      engine.OnDependencyCheck?.Invoke(Name);
      return base.DependenciesReady();
    }

    public override bool IsStarted
    {
      get
      {
        engine.OnStartedLook?.Invoke(Name);
        return base.IsStarted;
      }
    }

    public override bool Equals(object obj)
    {
      engine.OnCompare?.Invoke();
      return base.Equals(obj);
    }

    public override int GetHashCode() => base.GetHashCode();
  }

  /// <summary>
  /// A task of the graph: its name and the names of the tasks it depends on, each given before it.
  /// </summary>
  public static (string, string[]) T(string name, params string[] dependsOn) => (name, dependsOn);

  /// <summary>
  /// Starts <paramref name="engine"/> and waits until it is ready.
  /// </summary>
  public static async Task<GraphEngine> ReadyAsync(GraphEngine engine)
  {
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    engine.State.Should().Be(EngineState.Ready);
    return engine;
  }
}
