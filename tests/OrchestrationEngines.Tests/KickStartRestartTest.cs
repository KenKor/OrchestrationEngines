using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Restarter;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for which engines the kick-start starts again when the descriptor's recovery retries it: a Faulted
/// engine, and an engine Stopped by a task's own cancellation (a transient error such as a timed-out call), but not an
/// engine that was stopped on purpose.
/// </summary>
public class KickStartRestartTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);
  private const string Name = "E";

  private static RestartPolicy NoWait(int maxRestarts) => new()
  {
    MaxRestartsInWindow = maxRestarts,
    Window = TimeSpan.FromMinutes(10),
    InitialBackoff = TimeSpan.Zero,
    MaxBackoff = TimeSpan.Zero,
  };

  /// <summary>
  /// An engine with one load task, whose work the test decides per load, counting its starts and loads.
  /// </summary>
  private sealed class NetworkEngine(Func<int, CancellationToken, Task> loadAsync) : BaseEngine(NullLogger.Instance)
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

    protected override void SetUpTasks() => CreateTask("Load", token => loadAsync(Interlocked.Increment(ref _loads), token));
  }

  private static EngineCoordinator CreateCoordinator(NetworkEngine engine)
  {
    var coordinator = new EngineCoordinator(nameof(KickStartRestartTest), NullLogger.Instance, useDispatcher: false);
    coordinator.RegisterEngine(Name, _ => Task.FromResult<IEngine>(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .SetRecoveryHelper(NoWait(maxRestarts: 5));
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
  /// Verifies that a lookup that arrives while the kick-start waits to retry a failed engine waits for the retry and
  /// gets the engine once it is Ready, instead of null (on a server, a failed query) a moment before it recovers.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_GetEngineAsync_DuringKickStartRetryWait_WaitsForTheRetry()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new NetworkEngine((load, _) => load == 1 ? throw new InvalidOperationException("The database was not reachable.") : Task.CompletedTask);
    var coordinator = new EngineCoordinator(nameof(KickStartRestartTest), NullLogger.Instance, useDispatcher: false);
    coordinator.RegisterEngine(Name, _ => Task.FromResult<IEngine>(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .SetRecoveryHelper(new RestartPolicy
      {
        MaxRestartsInWindow = 5,
        Window = TimeSpan.FromMinutes(10),
        InitialBackoff = TimeSpan.FromSeconds(2),
        MaxBackoff = TimeSpan.FromSeconds(2),
      });
    _ = coordinator.StartAsync();
    var deadline = DateTime.UtcNow + MustComplete;
    while (engine.State != EngineState.Faulted)
    {
      DateTime.UtcNow.Should().BeBefore(deadline, "the first load must fail");
      await Task.Delay(10, ct);
    }

    var loaded = await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct);

    loaded.Should().BeSameAs(engine);
    engine.State.Should().Be(EngineState.Ready);
    engine.Loads.Should().Be(2);
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
  /// how many watcher runs are active at the same time.
  /// </summary>
  private sealed class WatchedEngine(Func<int, Task> loadAsync) : BaseEngine(NullLogger.Instance)
  {
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
        try
        {
          await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        finally
        {
          Interlocked.Decrement(ref _activeWatchers);
        }
      }, requiredForRunning: false);
      CreateTask("Load", _ => loadAsync(Interlocked.Increment(ref _loads)));
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
    var coordinator = new EngineCoordinator(nameof(KickStartRestartTest), NullLogger.Instance, useDispatcher: false);
    coordinator.RegisterEngine(Name, _ => Task.FromResult<IEngine>(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .SetRecoveryHelper(NoWait(maxRestarts: 5));

    var loaded = await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct);
    var deadline = DateTime.UtcNow + MustComplete;
    while (engine.WatcherStarts < 2 || engine.ActiveWatchers != 1)
    {
      DateTime.UtcNow.Should().BeBefore(deadline, "the retry must start a new watcher and end the old one");
      await Task.Delay(10, ct);
    }

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
