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

    /// <summary>
    /// Runs as a task's reset begins (before it is back in Created), with the task's name.
    /// </summary>
    public Action<string> OnReset { get; set; }

    public int Runs(string name) => _runs.TryGetValue(name, out var runs) ? runs : 0;

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
      protected override Task OnResetEnteredAsync()
      {
        engine.OnReset?.Invoke(Name);
        return base.OnResetEnteredAsync();
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

  /// <summary>
  /// Verifies that a start fired while a batch is being reset waits for the whole batch: A failed and B with it; while A
  /// is reset (B already reset, A still failed) something starts B. B must wait for A's next run, not fail on the old
  /// one and stay failed.
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_StartDuringTheReset_WaitsForTheWholeBatch()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new GraphEngine(T("A"), T("B", "A"))
    {
      Work = (name, run, _) => name == "A" && run == 1
        ? throw new InvalidOperationException("The first run failed.")
        : Task.CompletedTask,
    };
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    engine.Tasks["B"].State.Should().Be(EngineTaskState.DependencyFailed);
    Task startDuringReset = null;
    engine.OnReset = name =>
    {
      if (name == "A")
        startDuringReset = engine.TriggerStartAsync([engine.Tasks["B"]]);
    };

    await engine.ResetTasksAsync(["A"], resetDependentTasks: true).WaitAsync(MustComplete, ct);
    await startDuringReset.WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    engine.Tasks["B"].State.Should().Be(EngineTaskState.CompletedWithSuccess);
    new[] { engine.Runs("A"), engine.Runs("B") }.Should().Equal(2, 1);
  }

  /// <summary>
  /// Verifies that a stopped engine's tasks are not reset: they would never run again.
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_StoppedEngine_ThrowsAndResetsNothing()
  {
    var engine = await ReadyAsync(Chain());
    await engine.StopAsync();

    var act = () => engine.ResetTasksAsync(["A"], resetDependentTasks: true);

    await act.Should().ThrowAsync<InvalidOperationException>();
    engine.Tasks.Values.Should().OnlyContain(t => t.State == EngineTaskState.CompletedWithSuccess);
  }

  /// <summary>
  /// Verifies that tasks added after the set-up in a cycle are refused before anything is reset.
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_CycleAddedAfterTheSetUp_ThrowsAndResetsNothing()
  {
    var engine = await ReadyAsync(Chain());
    engine.Add("D", "C");
    engine.Tasks["C"].AddDependsOn(engine.Tasks["D"]);

    var act = () => engine.ResetTasksAsync(["A"], resetDependentTasks: true);

    await act.Should().ThrowAsync<CyclicEngineTaskDependencyException>();
    new[] { "A", "B", "C" }.Should().OnlyContain(n => engine.Tasks[n].State == EngineTaskState.CompletedWithSuccess);
  }

  /// <summary>
  /// Verifies that a dependent whose reset throws does not keep its dependency from being reset, and that the caller
  /// gets the exception.
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_DependentsResetThrows_ResetsTheDependencyAndThrows()
  {
    var engine = await ReadyAsync(Chain());
    var aReset = false;
    engine.OnReset = name =>
    {
      if (name == "C")
        throw new InvalidOperationException("C's reset failed.");
      aReset |= name == "A";
    };

    var act = () => engine.ResetTasksAsync(["A"], resetDependentTasks: true);

    (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("C's reset failed.");
    aReset.Should().BeTrue("A is reset after its dependents, also when one of their resets fails");
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

  /// <summary>
  /// Verifies that an engine that never started (its context failed, so it has no tasks) throws an
  /// <see cref="InvalidOperationException"/>.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_ResetTasksForEngineAsync_EngineNeverStarted_Throws()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator(nameof(ResetTasksTest), NullLogger.Instance, useDispatcher: false);
    var descriptor = coordinator.RegisterEngine("Graph", _ => Task.FromResult<IEngine>(Chain()))
      .SetContextFactory(_ => throw new InvalidOperationException("No context."));
    // The context must fail for good, not be retried.
    descriptor.RestartPolicy = null;

    var act = () => coordinator.ResetTasksForEngineAsync("Graph", ["A"], resetDependentTasks: false, ct)
      .WaitAsync(MustComplete, ct);

    (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("has not started");
  }

  /// <summary>
  /// Verifies that a coordinator replaced by a generation switch resets the next generation's engine.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_ResetTasksForEngineAsync_AfterASwitch_ResetsTheNextGenerationsEngine()
  {
    var ct = TestContext.Current.CancellationToken;
    var engines = new ConcurrentQueue<GraphEngine>();
    var coordinator = new EngineCoordinator(nameof(ResetTasksTest), NullLogger.Instance, useDispatcher: false);
    coordinator.RegisterEngine("Graph", _ =>
      {
        var engine = Chain();
        engines.Enqueue(engine);
        return Task.FromResult<IEngine>(engine);
      })
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct)
      .WaitAsync(MustComplete, ct);
    try
    {
      await coordinator.ResetTasksForEngineAsync("Graph", ["C"], resetDependentTasks: false, ct).WaitAsync(MustComplete, ct);
      await next.EnsureEngineTaskReadyAsync("Graph", "C", ct).WaitAsync(MustComplete, ct);

      engines.Should().HaveCount(2);
      engines.Last().Runs("C").Should().Be(2);
      engines.First().Runs("C").Should().Be(1);
    }
    finally
    {
      await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
    }
  }
}
