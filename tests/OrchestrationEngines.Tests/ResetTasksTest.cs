using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for <see cref="BaseEngine.ResetTasksAsync"/> and
/// <see cref="EngineCoordinator.ResetTasksForEngineAsync"/>: named tasks are reset, with their dependents if asked,
/// dependents first, and run again.
/// </summary>
public class ResetTasksTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// An engine whose tasks and dependencies are given as a list, and which counts each task's runs.
  /// </summary>
  private sealed class GraphEngine : BaseEngine
  {
    private readonly (string Name, string[] DependsOn)[] _graph;
    private readonly ConcurrentDictionary<string, int> _runs = new(StringComparer.OrdinalIgnoreCase);

    public GraphEngine(params (string Name, string[] DependsOn)[] graph) : base(NullLogger.Instance)
    {
      _graph = graph;
      Context = new NoContext();
    }

    /// <summary>
    /// Runs after the count, with the task's name, its run number and its token.
    /// </summary>
    public Func<string, int, CancellationToken, Task> Work { get; init; }

    public int Runs(string name) => _runs.TryGetValue(name, out var runs) ? runs : 0;

    protected override void SetUpTasks()
    {
      var created = new Dictionary<string, EngineTask>();
      foreach (var (name, dependsOn) in _graph)
      {
        var task = CreateTask(name, token =>
        {
          var run = _runs.AddOrUpdate(name, 1, (_, n) => n + 1);
          return Work?.Invoke(name, run, token) ?? Task.CompletedTask;
        });
        foreach (var parent in dependsOn)
          task.AddDependsOn(created[parent]);
        created[name] = task;
      }
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

  private static GraphEngine Chain() => new(T("A"), T("B", "A"), T("C", "B"));

  /// <summary>
  /// Verifies that a task without dependents is reset and runs again, and the other tasks do not.
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_SingleTask_RunsItAgain()
  {
    var engine = await ReadyAsync(new GraphEngine(T("A"), T("B")));

    await engine.ResetTasksAsync(["A"], resetDependentTasks: true);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    engine.Runs("A").Should().Be(2);
    engine.Runs("B").Should().Be(1);
    engine.Tasks["A"].State.Should().Be(EngineTaskState.CompletedWithSuccess);
  }

  /// <summary>
  /// Verifies that resetting the middle of a chain A ← B ← C without its dependents runs only B again.
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_ChainMiddleWithoutDependents_RunsOnlyIt()
  {
    var engine = await ReadyAsync(Chain());

    await engine.ResetTasksAsync(["b"], resetDependentTasks: false);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    new[] { engine.Runs("A"), engine.Runs("B"), engine.Runs("C") }.Should().Equal(1, 2, 1);
  }

  /// <summary>
  /// Verifies that resetting the middle of a chain A ← B ← C with its dependents runs B and C again, not A.
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_ChainMiddleWithDependents_RunsItAndItsDependents()
  {
    var engine = await ReadyAsync(Chain());

    await engine.ResetTasksAsync(["B"], resetDependentTasks: true);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    new[] { engine.Runs("A"), engine.Runs("B"), engine.Runs("C") }.Should().Equal(1, 2, 2);
    engine.Tasks.Values.Should().OnlyContain(t => t.State == EngineTaskState.CompletedWithSuccess);
  }

  /// <summary>
  /// Verifies that in a diamond (B and C on A, D on both), resetting B and C with their dependents runs the shared
  /// dependent D once more, not twice.
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_Diamond_ResetsTheSharedDependentOnce()
  {
    var engine = await ReadyAsync(new GraphEngine(T("A"), T("B", "A"), T("C", "A"), T("D", "B", "C")));

    await engine.ResetTasksAsync(["B", "C", "b"], resetDependentTasks: true);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    new[] { engine.Runs("A"), engine.Runs("B"), engine.Runs("C"), engine.Runs("D") }.Should().Equal(1, 2, 2, 2);
  }

  /// <summary>
  /// Verifies that a dependent is reset before the task it depends on: when the running dependent B is reset (its token
  /// cancelled), A, which B depends on and which comes first in the set, has not been reset yet.
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_ResetsDependentsFirst()
  {
    var ct = TestContext.Current.CancellationToken;
    var bRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    EngineTaskState? aWhenBWasReset = null;
    GraphEngine engine = null;
    engine = new GraphEngine(T("A"), T("B", "A"))
    {
      Work = async (name, run, token) =>
      {
        if (name != "B" || run != 1)
          return;
        // Runs inside B's reset, which cancels the token.
        token.Register(() => aWhenBWasReset = engine.Tasks["A"].State);
        bRunning.SetResult();
        await Task.Delay(Timeout.Infinite, token);
      },
    };
    await engine.StartAsync();
    await bRunning.Task.WaitAsync(MustComplete, ct);

    await engine.ResetTasksAsync(["A"], resetDependentTasks: true);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    aWhenBWasReset.Should().Be(EngineTaskState.CompletedWithSuccess, "A must be reset only after B");
    new[] { engine.Runs("A"), engine.Runs("B") }.Should().Equal(2, 2);
  }

  /// <summary>
  /// Verifies that a task reset while its work runs drops that run's outcome: the first run fails after the reset, and
  /// the task still ends with the next run's success.
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_WhileTheWorkRuns_DropsTheOldRunsOutcome()
  {
    var ct = TestContext.Current.CancellationToken;
    var firstRunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var firstRunGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = new GraphEngine(T("A"))
    {
      Work = async (_, run, _) =>
      {
        if (run != 1)
          return;
        firstRunStarted.SetResult();
        // Ignores its token, and fails once the reset has happened.
        await firstRunGate.Task;
        throw new InvalidOperationException("The first run failed.");
      },
    };
    await engine.StartAsync();
    await firstRunStarted.Task.WaitAsync(MustComplete, ct);

    Task reset;
    try
    {
      reset = engine.ResetTasksAsync(["A"], resetDependentTasks: false);
    }
    finally
    {
      firstRunGate.SetResult();
    }
    await reset.WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    engine.Tasks["A"].State.Should().Be(EngineTaskState.CompletedWithSuccess);
    engine.Runs("A").Should().Be(2);
  }

  /// <summary>
  /// Verifies that an unknown name throws an <see cref="ArgumentException"/>, and nothing is reset.
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_UnknownName_ThrowsAndResetsNothing()
  {
    var engine = await ReadyAsync(Chain());

    var act = () => engine.ResetTasksAsync(["A", "Missing"], resetDependentTasks: true);

    (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("Missing");
    engine.Tasks.Values.Should().OnlyContain(t => t.State == EngineTaskState.CompletedWithSuccess);
    engine.Runs("A").Should().Be(1);
  }

  /// <summary>
  /// Verifies that a reset of a Ready engine is announced: Ready, then Starting while the tasks run again, then Ready.
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_ReadyEngine_GoesStartingThenReady()
  {
    var engine = await ReadyAsync(Chain());
    var states = new ConcurrentQueue<EngineState>();
    engine.EngineStatusChanged += (_, e) => states.Enqueue(e.State);

    await engine.ResetTasksAsync(["B"], resetDependentTasks: true);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    states.Should().Equal(EngineState.Starting, EngineState.Ready);
  }

  private static EngineCoordinator CoordinatorWith(IEngine engine)
  {
    var coordinator = new EngineCoordinator(nameof(ResetTasksTest), NullLogger.Instance, useDispatcher: false);
    coordinator.RegisterEngine("Graph", _ => Task.FromResult(engine))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));
    return coordinator;
  }

  /// <summary>
  /// Verifies that the coordinator, not started yet, starts and resets the engine's tasks with their dependents.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_ResetTasksForEngineAsync_ResetsTheEnginesTasks()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = Chain();
    var coordinator = CoordinatorWith(engine);

    await coordinator.ResetTasksForEngineAsync("graph", ["B"], resetDependentTasks: true, ct).WaitAsync(MustComplete, ct);
    await coordinator.EnsureEngineTaskReadyAsync("Graph", "C", ct).WaitAsync(MustComplete, ct);

    new[] { engine.Runs("A"), engine.Runs("B"), engine.Runs("C") }.Should().Equal(1, 2, 2);
  }

  /// <summary>
  /// Verifies that an unknown engine throws an <see cref="ArgumentException"/>.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_ResetTasksForEngineAsync_UnknownEngine_Throws()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CoordinatorWith(Chain());

    var act = () => coordinator.ResetTasksForEngineAsync("Missing", ["A"], resetDependentTasks: false, ct);

    await act.Should().ThrowAsync<ArgumentException>();
  }
}
