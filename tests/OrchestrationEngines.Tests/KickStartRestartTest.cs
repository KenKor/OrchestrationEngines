using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Restarter;
using OrchestrationEngines.Tasks;
using Xunit;
using static OrchestrationEngines.Tests.TestRestartPolicies;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for which engines the kick-start starts again when the descriptor's recovery retries it: a Faulted
/// engine, and an engine Stopped by a task's own cancellation (a transient error such as a timed-out call), but not an
/// engine that was stopped on purpose.
/// </summary>
public class KickStartRestartTest
{
  private const string Name = "E";

  /// <summary>
  /// An engine with one load task, whose work the test decides per load, counting its starts and loads.
  /// </summary>
  /// <param name="loadAsync">The work of a load, given the load's number (from 1).</param>
  /// <param name="extraAsync">If set, the work of a task "Extra" that is not required for running.</param>
  private sealed class NetworkEngine(Func<int, CancellationToken, Task> loadAsync,
    Func<CancellationToken, Task> extraAsync = null) : BaseEngine(NullLogger.Instance)
  {
    private int _starts;
    private int _loads;

    public int Starts => Volatile.Read(ref _starts);

    public int Loads => Volatile.Read(ref _loads);

    public override Task StartAsync()
    {
      Interlocked.Increment(ref _starts);
      return base.StartAsync();
    }

    protected override void SetUpTasks()
    {
      CreateTask("Load", token => loadAsync(Interlocked.Increment(ref _loads), token));
      if (extraAsync != null)
        CreateTask("Extra", extraAsync, requiredForRunning: false);
    }
  }

  /// <summary>
  /// An engine whose optional load throws an EngineTaskException, and a required index that depends on the load.
  /// </summary>
  private sealed class OptionalLoadEngine() : BaseEngine(NullLogger.Instance)
  {
    private int _loads;

    public int Loads => Volatile.Read(ref _loads);

    protected override void SetUpTasks()
    {
      var load = CreateTask("Load", _ =>
      {
        Interlocked.Increment(ref _loads);
        throw new TestTaskException();
      }, requiredForRunning: false);
      CreateTask("Index", _ => Task.CompletedTask).AddDependsOn(load);
    }
  }

  private static EngineCoordinator CreateCoordinator(IEngine engine)
  {
    var coordinator = new EngineCoordinator(nameof(KickStartRestartTest), NullLogger.Instance);
    coordinator.RegisterEngine(Name, _ => Task.FromResult<IEngine>(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .SetRestartPolicy(NoWait(maxRestarts: 5));
    return coordinator;
  }

  /// <summary>
  /// Verifies that an engine whose load was cancelled by something else than the engine (a timed-out call throwing an
  /// OperationCanceledException), which leaves it Stopped, is started again by the descriptor's recovery and ends
  /// Ready.
  /// </summary>
  [Fact]
  public async Task EngineKickStartTask_LoadCancelledWhileEngineLive_StartsTheEngineAgain()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new NetworkEngine((load, _) => load == 1 ? throw new TaskCanceledException("The request timed out.") : Task.CompletedTask);
    var coordinator = CreateCoordinator(engine);

    var loaded = await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct);

    loaded.Should().BeSameAs(engine);
    engine.State.Should().Be(EngineState.Ready);
    engine.Loads.Should().Be(2);
  }

  /// <summary>
  /// Verifies that an engine whose load throws an EngineTaskException is not started again (#90): the load task ends
  /// Cancelled without a retry, which stops the engine, and the kick-start does not start it again under the
  /// descriptor's restart policy either, since another attempt cannot change the condition. The kick-start ends Failed,
  /// so the coordinator hears of it and goes back to WaitingForEngines instead of staying in Initializing. A lookup
  /// gets null.
  /// </summary>
  [Fact]
  public async Task EngineKickStartTask_LoadThrowsEngineTaskException_DoesNotStartTheEngineAgain()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new NetworkEngine((load, _) => load == 1 ? throw new TestTaskException() : Task.CompletedTask);
    var coordinator = CreateCoordinator(engine);

    var loaded = await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct);

    loaded.Should().BeNull();
    engine.Starts.Should().Be(1);
    engine.Loads.Should().Be(1);
    engine.State.Should().Be(EngineState.Stopped);
    await Wait.UntilAsync(() => coordinator.State == EngineCoordinatorState.WaitingForEngines,
      MustComplete, "the failed kick-start must reach the coordinator");
    engine.Starts.Should().Be(1);
  }

  /// <summary>
  /// Verifies that a task not required for running whose work throws an EngineTaskException does not keep the
  /// kick-start from starting the engine again when a required task stopped it for another reason (a timed-out call):
  /// only the engine's required tasks decide whether it is started again.
  /// </summary>
  [Fact]
  public async Task EngineKickStartTask_OptionalTaskCancelledByItsWork_StillStartsTheEngineAgain()
  {
    var ct = TestContext.Current.CancellationToken;
    NetworkEngine engine = null;
    engine = new NetworkEngine(
      async (load, _) =>
      {
        if (load > 1)
          return;
        // The engine stops only once the optional task has ended, so the kick-start sees both.
        await Wait.UntilAsync(() => engine.Tasks["Extra"].State == EngineTaskState.Cancelled, MustComplete,
          "the optional task must end first");
        throw new TaskCanceledException("The request timed out.");
      },
      _ => throw new TestTaskException());
    var coordinator = CreateCoordinator(engine);

    var loaded = await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct);

    loaded.Should().BeSameAs(engine);
    engine.State.Should().Be(EngineState.Ready);
    engine.Loads.Should().Be(2);
  }

  /// <summary>
  /// Verifies that an engine is not started again when a task not required for running is cancelled by its own work
  /// and a required task depends on it: the required task then ends DependencyFailed, which stops the engine, and
  /// another start would run the optional task into the same condition.
  /// </summary>
  [Fact]
  public async Task EngineKickStartTask_RequiredTaskDependsOnTaskCancelledByItsWork_DoesNotStartTheEngineAgain()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new OptionalLoadEngine();
    var coordinator = CreateCoordinator(engine);

    var loaded = await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct);

    loaded.Should().BeNull();
    await Wait.UntilAsync(() => coordinator.State == EngineCoordinatorState.WaitingForEngines,
      MustComplete, "the failed kick-start must reach the coordinator");
    engine.Loads.Should().Be(1);
  }

  /// <summary>
  /// Verifies that a reset of the task of an engine that its own work stopped (an EngineTaskException), which the
  /// kick-start did not start again, runs the task again: once the condition has been dealt with, the host can start
  /// the engine by resetting the task.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_ResetTasksForEngineAsync_AfterTaskCancelledByItsWork_RunsTheEngineAgain()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new NetworkEngine((load, _) => load == 1 ? throw new TestTaskException() : Task.CompletedTask);
    var coordinator = CreateCoordinator(engine);
    (await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct)).Should().BeNull();

    await coordinator.ResetTasksForEngineAsync(Name, ["Load"], resetDependentTasks: true, ct).WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    engine.State.Should().Be(EngineState.Ready);
    engine.Loads.Should().Be(2);
  }

  /// <summary>
  /// Verifies that the coordinator is Running again once a reset has brought an engine whose kick-start failed back to
  /// Ready (#127): its state follows the engine, not the record of the failed start.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_ResetTasksForEngineAsync_AfterFailedKickStart_CoordinatorIsRunningAgain()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new NetworkEngine((load, _) => load == 1 ? throw new TestTaskException() : Task.CompletedTask);
    var coordinator = CreateCoordinator(engine);
    (await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct)).Should().BeNull();
    coordinator.State.Should().Be(EngineCoordinatorState.WaitingForEngines);

    await coordinator.ResetTasksForEngineAsync(Name, ["Load"], resetDependentTasks: true, ct)
      .WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Verifies that a lookup that arrives while the kick-start waits to retry a failed engine waits for the retry and
  /// gets the engine once it is Ready, instead of null (on a server, a failed query) a moment before it recovers.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_GetEngineAsync_DuringKickStartRetryWait_WaitsForTheRetry()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new NetworkEngine((load, _) => load == 1 ? throw new InvalidOperationException("The database was not reachable.") : Task.CompletedTask);
    var coordinator = new EngineCoordinator(nameof(KickStartRestartTest), NullLogger.Instance);
    coordinator.RegisterEngine(Name, _ => Task.FromResult<IEngine>(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .SetRestartPolicy(new RestartPolicy
      {
        MaxRestartsInWindow = 5,
        Window = TimeSpan.FromMinutes(10),
        InitialBackoff = TimeSpan.FromSeconds(2),
        MaxBackoff = TimeSpan.FromSeconds(2),
      });
    _ = coordinator.StartAsync();
    await Wait.UntilAsync(() => engine.State == EngineState.Faulted, MustComplete, "the first load must fail");

    var loaded = await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct);

    loaded.Should().BeSameAs(engine);
    engine.State.Should().Be(EngineState.Ready);
    engine.Loads.Should().Be(2);
  }

  /// <summary>
  /// Verifies that a required engine that failed, while its kick-start waits to retry it, counts as starting: the
  /// coordinator is Initializing, as a lookup waits for the retry. The backoff is long, so the retry is still pending.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_State_DuringKickStartRetryWait_IsInitializing()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new NetworkEngine((_, _) => throw new InvalidOperationException("The database was not reachable."));
    var coordinator = new EngineCoordinator(nameof(KickStartRestartTest), NullLogger.Instance);
    coordinator.RegisterEngine(Name, _ => Task.FromResult<IEngine>(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .SetRestartPolicy(new RestartPolicy
      {
        MaxRestartsInWindow = 5,
        Window = TimeSpan.FromMinutes(10),
        InitialBackoff = TimeSpan.FromMinutes(5),
        MaxBackoff = TimeSpan.FromMinutes(5),
      });
    _ = coordinator.StartAsync();
    await Wait.UntilAsync(() => engine.State == EngineState.Faulted, MustComplete, "the first load must fail");

    coordinator.State.Should().Be(EngineCoordinatorState.Initializing);
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that a lookup for an engine whose kick-start has given up returns null at once.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_GetEngineAsync_KickStartGaveUp_ReturnsNull()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new NetworkEngine((_, _) => throw new InvalidOperationException("The database was not reachable."));
    var coordinator = CreateCoordinator(engine);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var loaded = await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct);

    loaded.Should().BeNull();
    engine.State.Should().Be(EngineState.Faulted);
  }

  /// <summary>
  /// An engine with a required load and a background watcher that keeps running until its run is cancelled, counting
  /// how many watcher runs are active at the same time. The load waits until a watcher runs.
  /// </summary>
  private sealed class WatchedEngine(Func<int, Task> loadAsync) : BaseEngine(NullLogger.Instance)
  {
    private readonly TaskCompletionSource<bool> _watcherRunning = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _loads;
    private int _activeWatchers;
    private int _watcherStarts;

    public int ActiveWatchers => Volatile.Read(ref _activeWatchers);

    public int WatcherStarts => Volatile.Read(ref _watcherStarts);

    protected override void SetUpTasks()
    {
      CreateTask("Watch", async token =>
      {
        Interlocked.Increment(ref _watcherStarts);
        Interlocked.Increment(ref _activeWatchers);
        _watcherRunning.TrySetResult(true);
        try
        {
          await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        finally
        {
          Interlocked.Decrement(ref _activeWatchers);
        }
      }, requiredForRunning: false);
      // Fix (KickStartRestartTest): on a busy machine the first load could fail before the first watcher ran, so there
      // was no old watcher to end and the test waited for a second watcher start that never came.
      CreateTask("Load", async _ =>
      {
        await _watcherRunning.Task.WaitAsync(MustComplete, CancellationToken.None);
        await loadAsync(Interlocked.Increment(ref _loads));
      });
    }
  }

  /// <summary>
  /// Verifies that the kick-start's retry of an engine whose required task failed, while its background watcher kept
  /// running, ends that watcher's run: the engine starts with a new task set, and the old watcher must not run on next
  /// to the new one.
  /// </summary>
  [Fact]
  public async Task EngineKickStartTask_RetryAfterFailure_EndsTheOldBackgroundRun()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new WatchedEngine(load => load == 1 ? throw new InvalidOperationException("The load failed.") : Task.CompletedTask);
    var coordinator = new EngineCoordinator(nameof(KickStartRestartTest), NullLogger.Instance);
    coordinator.RegisterEngine(Name, _ => Task.FromResult<IEngine>(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .SetRestartPolicy(NoWait(maxRestarts: 5));

    var loaded = await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct);
    await Wait.UntilAsync(() => engine.WatcherStarts >= 2 && engine.ActiveWatchers == 1, MustComplete,
      "the retry must start a new watcher and end the old one");

    loaded.Should().BeSameAs(engine);
    engine.State.Should().Be(EngineState.Ready);
    await Task.Delay(100, ct);
    engine.ActiveWatchers.Should().Be(1, "only the new watcher runs");
    await engine.StopAsync();
  }

  /// <summary>
  /// Verifies that an engine stopped on purpose while it loads (its own token cancelled) is not started again by the
  /// descriptor's recovery: it stays Stopped and the lookup returns null.
  /// </summary>
  [Fact]
  public async Task EngineKickStartTask_EngineStoppedWhileLoading_IsNotStartedAgain()
  {
    var ct = TestContext.Current.CancellationToken;
    var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = new NetworkEngine(async (_, token) =>
    {
      loading.TrySetResult();
      await Task.Delay(Timeout.InfiniteTimeSpan, token);
    });
    var coordinator = CreateCoordinator(engine);
    var lookup = coordinator.GetEngineAsync(Name, ct);
    await loading.Task.WaitAsync(MustComplete, ct);

    await engine.StopAsync();
    var loaded = await lookup.WaitAsync(MustComplete, ct);

    loaded.Should().BeNull();
    engine.State.Should().Be(EngineState.Stopped);
    engine.Starts.Should().Be(1, "a stopped engine is not started again");
  }
}
