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

  /// <summary>
  /// An engine whose second set-up fails.
  /// </summary>
  private sealed class FailingSetUpEngine() : BaseEngine(NullLogger.Instance)
  {
    private int _setUps;

    protected override void SetUpTasks()
    {
      if (Interlocked.Increment(ref _setUps) == 2)
        throw new InvalidOperationException("The second set-up fails.");
      CreateTask("Load", _ => throw new InvalidOperationException("The load fails."));
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
    await Wait.UntilAsync(() => engine.ActiveWatchers == 1, MustComplete, "the watcher must run");
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
    await Wait.UntilAsync(() => engine.ActiveWatchers == 0, MustComplete, "the replaced set's watcher must stop");
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
    await Wait.UntilAsync(() => engine.ActiveWatchers == 1, MustComplete, "only the new set's watcher runs");
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
    await Wait.UntilAsync(() => engine.ActiveWatchers == 1, MustComplete, "only the new set's watcher runs");
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

  /// <summary>
  /// Verifies that a reset asked for while a restart is prepared waits until the engine has started again, and then
  /// returns without resetting the new set: its tasks run fresh, which is what the reset asked for (#58 item 4).
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_DuringPreparedRestart_WaitsForTheNewSet()
  {
    var engine = await FailedEngineAsync();
    await engine.ResetFailedTasksAsync();

    var reset = engine.ResetTasksAsync(["Load"], resetDependentTasks: true);
    await Task.Delay(MustStayPending, TestContext.Current.CancellationToken);
    reset.IsCompleted.Should().BeFalse("the engine has not started again yet");
    await engine.StartAsync();

    await reset.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    engine.State.Should().Be(EngineState.Ready);
    engine.Loads.Should().Be(2, "the new set is not reset and run again");
    await engine.StopAsync();
  }

  /// <summary>
  /// Verifies that a reset asked for after the restart has published its new set, while it still starts it, returns
  /// once the restart is done: the engine did start again (#58 item 4).
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_AfterTheRestartPublishedItsSet_Returns()
  {
    var engine = await FailedEngineAsync();
    await engine.ResetFailedTasksAsync();
    Task reset = null;
    // Starting is published after the new set and before the restart is done (see StartAsync).
    engine.EngineStatusChanged += (_, e) =>
    {
      if (e.State == EngineState.Starting)
        reset ??= engine.ResetTasksAsync(["Load"], resetDependentTasks: true);
    };

    await engine.StartAsync();

    reset.Should().NotBeNull("the engine went to Starting");
    await reset.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    engine.State.Should().Be(EngineState.Ready);
    engine.Loads.Should().Be(2, "the new set is not reset and run again");
    await engine.StopAsync();
  }

  /// <summary>
  /// Verifies that a reset asked for while a restart is prepared throws once the restart is given up, since nothing it
  /// asked for runs (#58 item 4).
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_DuringAbandonedRestart_Throws()
  {
    var engine = await FailedEngineAsync();
    await engine.ResetFailedTasksAsync();

    var reset = engine.ResetTasksAsync(["Load"], resetDependentTasks: true);
    await Task.Delay(MustStayPending, TestContext.Current.CancellationToken);
    reset.IsCompleted.Should().BeFalse("the restart has not been given up yet");
    engine.AbandonRestart("the test gives it up");

    var thrown = await reset.Invoking(r => r.WaitAsync(MustComplete, TestContext.Current.CancellationToken))
      .Should().ThrowAsync<InvalidOperationException>();
    thrown.Which.Message.Should().Contain("is not started again");
    thrown.Which.InnerException!.Message.Should().Contain("the test gives it up");
    engine.State.Should().Be(EngineState.Faulted);
    engine.Loads.Should().Be(1);
  }

  /// <summary>
  /// Verifies that a reset asked for while a restart is prepared throws, with the set-up's exception inside, when the
  /// restart's set-up fails: no new set was built, so nothing it asked for runs (#58 item 4).
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_DuringRestartWhoseSetUpFails_Throws()
  {
    var engine = new FailingSetUpEngine { Context = new NoContext() };
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    engine.State.Should().Be(EngineState.Faulted);
    await engine.ResetFailedTasksAsync();

    var reset = engine.ResetTasksAsync(["Load"], resetDependentTasks: true);
    await Task.Delay(MustStayPending, TestContext.Current.CancellationToken);
    reset.IsCompleted.Should().BeFalse("the engine has not started again yet");
    await engine.Invoking(e => e.StartAsync()).Should().ThrowAsync<InvalidOperationException>();

    var thrown = await reset.Invoking(r => r.WaitAsync(MustComplete, TestContext.Current.CancellationToken))
      .Should().ThrowAsync<InvalidOperationException>();
    thrown.Which.InnerException.Should().BeOfType<InvalidOperationException>()
      .Which.Message.Should().Be("The second set-up fails.");
  }

  /// <summary>
  /// Verifies that a reset asked for while a restart is prepared throws the "stopped" exception when the engine is
  /// stopped instead of started again (#58 item 4, with items 5 and 11).
  /// </summary>
  [Fact]
  public async Task BaseEngine_ResetTasksAsync_DuringPreparedRestart_ThenStopped_ThrowsStopped()
  {
    var engine = await FailedEngineAsync();
    await engine.ResetFailedTasksAsync();

    var reset = engine.ResetTasksAsync(["Load"], resetDependentTasks: true);
    await engine.StopAsync();

    var thrown = await reset.Invoking(r => r.WaitAsync(MustComplete, TestContext.Current.CancellationToken))
      .Should().ThrowAsync<InvalidOperationException>();
    thrown.Which.Message.Should().Contain("is stopped");
    engine.State.Should().Be(EngineState.Stopped);
  }
}
