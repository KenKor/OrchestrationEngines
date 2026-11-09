using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for an engine that is started again with a new task set, as the kick-start's retry does after a failed
/// load (prepare the restart, then start): the replaced set must not run again, and whoever waits on the engine or on
/// the replaced set must not wait forever.
/// </summary>
public class TaskSetReplacementTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan MustStayPending = TimeSpan.FromMilliseconds(200);

  /// <summary>
  /// An engine with a required load that fails its first run, and a background watcher that runs until its run is
  /// cancelled; counts the loads and the active watcher runs.
  /// </summary>
  private sealed class RetriedEngine() : BaseEngine(NullLogger.Instance)
  {
    private int _loads;
    private int _activeWatchers;

    public int Loads => Volatile.Read(ref _loads);

    public int ActiveWatchers => Volatile.Read(ref _activeWatchers);

    public EngineTask Load => Tasks["Load"];

    public EngineTask Watch => Tasks["Watch"];

    protected override void SetUpTasks()
    {
      CreateTask("Load", _ => Interlocked.Increment(ref _loads) == 1
        ? throw new InvalidOperationException("The first load fails.")
        : Task.CompletedTask);
      CreateTask("Watch", async token =>
      {
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
    }
  }

  private static async Task WaitUntilAsync(Func<bool> condition, string because)
  {
    var deadline = DateTime.UtcNow + MustComplete;
    while (!condition())
    {
      DateTime.UtcNow.Should().BeBefore(deadline, because);
      await Task.Delay(10, TestContext.Current.CancellationToken);
    }
  }

  /// <summary>
  /// Starts the engine and waits until its first load has failed while its watcher runs.
  /// </summary>
  private static async Task<RetriedEngine> FailedEngineAsync()
  {
    var engine = new RetriedEngine { Context = new NoContext() };
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => engine.ActiveWatchers == 1, "the watcher must run");
    engine.State.Should().Be(EngineState.Faulted);
    return engine;
  }

  /// <summary>
  /// Verifies that waiting until the engine is ready, begun after the restart was prepared and before the engine started
  /// again, ends once the new task set is ready, instead of waiting on the set that the start throws away.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureReadyAsync_DuringPreparedRestart_EndsWithTheNewSet()
  {
    var engine = await FailedEngineAsync();
    await engine.ResetFailedTasksAsync();

    var waiting = engine.EnsureReadyAsync();
    await Task.Delay(MustStayPending, TestContext.Current.CancellationToken);
    waiting.IsCompleted.Should().BeFalse("the engine has not started again yet");
    await engine.StartAsync();

    await waiting.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    engine.State.Should().Be(EngineState.Ready);
    engine.Loads.Should().Be(2);
  }

  /// <summary>
  /// Verifies that preparing a restart ends the replaced set's running tasks (the watcher) and releases whoever waits on
  /// them, and that nothing starts the replaced set again before the engine starts with a new one.
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetFailedTasksAsync_EndsTheSetAndNothingStartsItAgain()
  {
    var engine = await FailedEngineAsync();
    var oldLoad = engine.Load;
    var oldWatcherWaiter = engine.Watch.InnerTask;

    await engine.ResetFailedTasksAsync();

    await oldWatcherWaiter.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => engine.ActiveWatchers == 0, "the replaced set's watcher must stop");
    engine.State.Should().Be(EngineState.Created);
    await engine.TriggerStartAsync();
    await engine.TriggerResetAsync([oldLoad]);
    await Task.Delay(MustStayPending, TestContext.Current.CancellationToken);
    engine.Loads.Should().Be(1, "the replaced set is not run again");
    oldLoad.IsCompleted.Should().BeTrue();

    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    engine.State.Should().Be(EngineState.Ready);
    engine.Loads.Should().Be(2);
    await WaitUntilAsync(() => engine.ActiveWatchers == 1, "only the new set's watcher runs");
    await engine.StopAsync();
  }

  /// <summary>
  /// Verifies that a start that replaces a set whose tasks someone started or waits on (without a prepared restart)
  /// ends them, so that waiter is released.
  /// </summary>
  [Fact]
  public async Task BaseEngine_StartAsync_ReplacingASetWithAWaiter_ReleasesTheWaiter()
  {
    var engine = await FailedEngineAsync();
    var oldWatcherWaiter = engine.Watch.InnerTask;

    await engine.StartAsync();

    await oldWatcherWaiter.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    engine.State.Should().Be(EngineState.Ready);
    await WaitUntilAsync(() => engine.ActiveWatchers == 1, "only the new set's watcher runs");
    await engine.StopAsync();
  }

  /// <summary>
  /// Verifies that stopping an engine whose restart was prepared but never started releases whoever waits until it is
  /// ready.
  /// </summary>
  [Fact]
  public async Task BaseEngine_StopAsync_DuringPreparedRestart_ReleasesTheWaiter()
  {
    var engine = await FailedEngineAsync();
    await engine.ResetFailedTasksAsync();
    var waiting = engine.EnsureReadyAsync();

    await engine.StopAsync();

    await waiting.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    engine.State.Should().Be(EngineState.Stopped);
  }
}
