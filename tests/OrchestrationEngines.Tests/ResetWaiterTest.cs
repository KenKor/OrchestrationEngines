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
/// Contains tests for whoever waits on a task that is reset or stopped before it completes: the wait must end, with the
/// next run after a reset, or at once after a stop.
/// </summary>
public class ResetWaiterTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan MustStayPending = TimeSpan.FromMilliseconds(200);

  /// <summary>
  /// A gate the work of a run waits for; each run gets a fresh one.
  /// </summary>
  private sealed class Runs
  {
    private readonly TaskCompletionSource[] _gates =
      [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
    private int _runs;

    public int Count => Volatile.Read(ref _runs);

    public Task Next() => _gates[Interlocked.Increment(ref _runs) - 1].Task;

    public void Complete(int run) => _gates[run - 1].TrySetResult();
  }

  /// <summary>
  /// An engine with one required load that waits for its run's gate.
  /// </summary>
  private sealed class GatedEngine(Runs runs) : BaseEngine(NullLogger.Instance)
  {
    public EngineTask Load => Tasks["Load"];

    protected override void SetUpTasks() => CreateTask("Load", _ => runs.Next());
  }

  /// <summary>
  /// An engine whose load ignores its token (it only ends when the test says so), and an index that depends on it.
  /// </summary>
  private sealed class StuckLoadEngine(Task loadGate) : BaseEngine(NullLogger.Instance)
  {
    public EngineTask Index => Tasks["Index"];

    protected override void SetUpTasks()
    {
      var load = CreateTask("Load", _ => loadGate);
      CreateTask("Index", _ => Task.CompletedTask).AddDependsOn(load);
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
  /// Verifies that whoever waits on a running task that is then reset is released when the task's next run completes,
  /// instead of waiting forever on the completion of the run that the reset ended; and that the next run's work only
  /// starts once the ended run's work (which ignores its token) has ended.
  /// </summary>
  [Fact]
  public async Task EngineTask_ResetWhileRunning_EarlierWaiterIsReleasedByTheNextRun()
  {
    var runs = new Runs();
    var task = new EngineTask("Load", _ => runs.Next(), requiredForRunning: true, NullLogger.Instance, CancellationToken.None);
    await task.FireAsync(EngineTaskTrigger.Start);
    await WaitUntilAsync(() => runs.Count == 1, "the first run must have started");
    var earlierWaiter = task.InnerTask;

    await task.FireAsync(EngineTaskTrigger.Reset);
    await task.FireAsync(EngineTaskTrigger.Start);
    await Task.Delay(MustStayPending, TestContext.Current.CancellationToken);
    runs.Count.Should().Be(1, "the next run waits until the ended run's work has ended");
    (task.StateMessage ?? "").Should().NotContain("previous run", "the message only changes once the wait takes long");
    runs.Complete(1);
    await WaitUntilAsync(() => runs.Count == 2, "the next run must start once the ended run's work has ended");
    await Task.Delay(MustStayPending, TestContext.Current.CancellationToken);
    earlierWaiter.IsCompleted.Should().BeFalse("the run that the reset ended does not complete the wait");

    runs.Complete(2);

    await earlierWaiter.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
  }

  /// <summary>
  /// Verifies that waiting until an engine is ready, begun while its load runs, ends when the load runs again after a
  /// reset (as a context change or a change monitor does) and completes.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureReadyAsync_ResetWhileWaiting_EndsWithTheNextRun()
  {
    var runs = new Runs();
    var engine = new GatedEngine(runs) { Context = new NoContext() };
    await engine.StartAsync();
    await WaitUntilAsync(() => runs.Count == 1, "the first run must have started");
    var waiting = engine.EnsureReadyAsync();

    await engine.TriggerResetAsync([engine.Load]);
    // The first run's work ignores its token: the load runs again once it has ended.
    runs.Complete(1);
    await WaitUntilAsync(() => runs.Count == 2, "the reset must start the load again");
    runs.Complete(2);

    await waiting.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    engine.State.Should().Be(EngineState.Ready);
  }

  /// <summary>
  /// Verifies that stopping an engine releases whoever waits on a task that waits for a dependency whose work ignores the
  /// token: that dependency never ends, so the waiting task would never fail through it.
  /// </summary>
  [Fact]
  public async Task BaseEngine_StopAsync_TaskWaitingForStuckDependency_IsReleased()
  {
    var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = new StuckLoadEngine(loadGate.Task) { Context = new NoContext() };
    await engine.StartAsync();
    engine.Index.State.Should().Be(EngineTaskState.WaitingForDependencies);
    var waiter = engine.Index.InnerTask;

    await engine.StopAsync();

    await waiter.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    engine.Index.IsFailed.Should().BeTrue();
    engine.State.Should().Be(EngineState.Stopped);
    loadGate.SetResult();
  }
}
