using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Restarter;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for a single engine task: its recovery, time-out, reset, and the late dependency signals it must
/// ignore.
/// </summary>
public class EngineTaskTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// A restart policy that never waits between restarts, so tests do not depend on the backoff.
  /// </summary>
  private static RestartPolicy NoWait(int maxRestarts) => new()
  {
    MaxRestartsInWindow = maxRestarts,
    Window = TimeSpan.FromMinutes(10),
    InitialBackoff = TimeSpan.Zero,
    MaxBackoff = TimeSpan.Zero,
  };

  private sealed class TestTaskException(EngineTask et) : EngineTaskException(et, "The task cannot go on.");

  private static EngineTask NewTask(string name, Func<CancellationToken, Task> workAsync, TimeProvider clock = null,
    CancellationToken engineToken = default) =>
    new(name, workAsync, requiredForRunning: true, NullLogger.Instance, engineToken, clock);

  private static async Task RunToEndAsync(EngineTask task)
  {
    await task.FireAsync(EngineTaskTrigger.Start);
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
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
  /// Verifies that a task with a recovery policy runs its work again after it failed, and completes when that run
  /// succeeds.
  /// </summary>
  [Fact]
  public async Task EngineTask_WorkThrows_WithRecovery_RunsTheWorkAgain()
  {
    var runs = 0;
    var task = NewTask("Load", _ => ++runs == 1 ? throw new InvalidOperationException("First run fails.") : Task.CompletedTask)
      .SetRecovery(NoWait(maxRestarts: 5));

    await RunToEndAsync(task);

    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
    runs.Should().Be(2);
  }

  /// <summary>
  /// Verifies that a task that keeps failing gives up after the number of restarts its policy allows, and ends in
  /// RecoveryFailed.
  /// </summary>
  [Fact]
  public async Task EngineTask_WorkThrows_LimitExceeded_EndsInRecoveryFailed()
  {
    var runs = 0;
    var task = NewTask("Load", _ =>
    {
      Interlocked.Increment(ref runs);
      throw new InvalidOperationException("Every run fails.");
    }).SetRecovery(NoWait(maxRestarts: 2));

    await RunToEndAsync(task);

    task.State.Should().Be(EngineTaskState.RecoveryFailed);
    runs.Should().Be(3, "the first run and the two restarts the policy allows");
  }

  /// <summary>
  /// Verifies that a task without a recovery policy that takes longer than its time-out ends in TimedOut.
  /// </summary>
  [Fact]
  public async Task EngineTask_TimesOut_WithoutRecovery_EndsInTimedOut()
  {
    var clock = new ManualTimeProvider();
    var task = NewTask("Load", token => Task.Delay(Timeout.Infinite, token), clock)
      .SetTimeOut(TimeSpan.FromMinutes(1));
    await task.FireAsync(EngineTaskTrigger.Start);
    await WaitUntilAsync(() => clock.WaitingTimers == 1, "the run must wait for its time-out");

    clock.Advance(TimeSpan.FromSeconds(59));
    await Task.Delay(100, TestContext.Current.CancellationToken);
    task.State.Should().Be(EngineTaskState.Running, "the time-out has not passed yet");
    clock.Advance(TimeSpan.FromSeconds(1));
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    task.State.Should().Be(EngineTaskState.TimedOut);
  }

  /// <summary>
  /// Verifies that a task with a recovery policy whose work times out and then ignores its cancellation (as task
  /// bodies often do) ends in TimedOut once a grace period has passed, instead of staying in Recovering while its
  /// recovery waits for that work for good. Whoever waits for the task is released, and the reason says why.
  /// </summary>
  [Fact]
  public async Task EngineTask_TimesOut_WithRecovery_WorkIgnoresCancellation_EndsInTimedOut()
  {
    var clock = new ManualTimeProvider();
    var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    try
    {
      var task = NewTask("Load", _ => hang.Task, clock)
        .SetTimeOut(TimeSpan.FromMinutes(1))
        .SetRecovery(new RestartPolicy { MaxRestartsInWindow = 3, Window = TimeSpan.FromMinutes(10) });
      await task.FireAsync(EngineTaskTrigger.Start);
      await WaitUntilAsync(() => clock.WaitingTimers == 1, "the run must wait for its time-out");

      clock.Advance(TimeSpan.FromMinutes(1));
      await Task.Delay(100, TestContext.Current.CancellationToken);
      clock.Advance(EngineTask.PreviousRunWarningAfter + TimeSpan.FromSeconds(1));
      await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);

      task.State.Should().Be(EngineTaskState.TimedOut);
      task.FailReason.Should().Contain("did not stop");
      hang.Task.IsCompleted.Should().BeFalse("the work was still running");
    }
    finally
    {
      hang.TrySetResult();
    }
  }

  /// <summary>
  /// Verifies that synchronous work (as task bodies often are) that takes longer than the time-out ends the task in
  /// TimedOut while the work still runs; before, the work ran to its end before the time-out was even started.
  /// </summary>
  [Fact]
  public async Task EngineTask_SynchronousWorkTimesOut_EndsInTimedOut()
  {
    var clock = new ManualTimeProvider();
    // Not disposed: the armed time-out does not prove the work has reached the gate yet, so it may do so after the test.
    var workGate = new ManualResetEventSlim();
    try
    {
      var task = NewTask("Load", _ =>
      {
        workGate.Wait(MustComplete);
        return Task.CompletedTask;
      }, clock).SetTimeOut(TimeSpan.FromMinutes(1));
      await task.FireAsync(EngineTaskTrigger.Start);
      await WaitUntilAsync(() => clock.WaitingTimers == 1, "the time-out must run while the work does");

      clock.Advance(TimeSpan.FromMinutes(1));
      await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);

      task.State.Should().Be(EngineTaskState.TimedOut);
      workGate.IsSet.Should().BeFalse("the work was still running");
    }
    finally
    {
      workGate.Set();
    }
  }

  /// <summary>
  /// Verifies that a task with a time-out whose engine is stopped before the time-out passes ends Cancelled, not
  /// TimedOut: whether its work ignores the token (and goes on) or honours it.
  /// </summary>
  /// <remarks>
  /// The stop cancels the time-out's delay too. The work ends only once the test has checked the task, so the cancelled
  /// delay always finishes the race first, and the guard that tells a stop from a time-out is what decides the state.
  /// Work that ended first would be reported as cancelled on its own, guard or not.
  /// </remarks>
  /// <param name="workHonoursToken">Whether the work observes its token (and ends cancelled), or ignores it.</param>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task EngineTask_WithTimeOut_EngineStoppedBeforeIt_EndsCancelledNotTimedOut(bool workHonoursToken)
  {
    var clock = new ManualTimeProvider();
    using var engineStop = new CancellationTokenSource();
    // Not disposed: work that honours its token reaches the gate on another thread, possibly after the test has ended.
    var workGate = new ManualResetEventSlim();
    try
    {
      var task = NewTask("Load", async token =>
      {
        if (!workHonoursToken)
        {
          // Synchronous work, as task bodies often are.
          workGate.Wait(MustComplete);
          return;
        }
        try
        {
          await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
          // On .NET Framework this catch runs inline in engineStop.Cancel(), and the wait below would block the test
          // there for its whole 5 s (#55). A cancellation handler should not block: let the stop return first.
          await Task.Yield();
          workGate.Wait(MustComplete);
          throw;
        }
      }, clock, engineStop.Token).SetTimeOut(TimeSpan.FromMinutes(90));
      await task.FireAsync(EngineTaskTrigger.Start);
      await WaitUntilAsync(() => clock.WaitingTimers == 1, "the time-out must run while the work does");

      engineStop.Cancel();
      await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);

      task.State.Should().Be(EngineTaskState.Cancelled);
      task.FailReason.Should().Be("Cancelled because the engine was stopped.");
      task.WasCancelled.Should().BeTrue();
    }
    finally
    {
      workGate.Set();
    }
  }

  /// <summary>
  /// Verifies that a task with a recovery policy that timed out runs again with a fresh token: the time-out
  /// cancelled the token of the first run, and the second run must not start cancelled.
  /// </summary>
  [Fact]
  public async Task EngineTask_TimesOut_WithRecovery_RecoversWithFreshToken()
  {
    var clock = new ManualTimeProvider();
    var runs = 0;
    CancellationToken firstToken = default, secondToken = default;
    var task = NewTask("Load", token =>
    {
      if (++runs == 1)
      {
        firstToken = token;
        return Task.Delay(Timeout.Infinite, token);
      }
      secondToken = token;
      return Task.CompletedTask;
    }, clock).SetTimeOut(TimeSpan.FromMinutes(1)).SetRecovery(NoWait(maxRestarts: 5));
    await task.FireAsync(EngineTaskTrigger.Start);
    await WaitUntilAsync(() => clock.WaitingTimers == 1, "the run must wait for its time-out");

    clock.Advance(TimeSpan.FromMinutes(1));
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
    firstToken.IsCancellationRequested.Should().BeTrue("the time-out cancels the run that took too long");
    secondToken.IsCancellationRequested.Should().BeFalse();
  }

  /// <summary>
  /// Verifies that the run times and duration come from the task's clock.
  /// </summary>
  [Fact]
  public async Task EngineTask_RunDuration_FollowsItsClock()
  {
    var clock = new ManualTimeProvider();
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var task = NewTask("Load", _ => gate.Task, clock);
    var started = clock.GetUtcNow().UtcDateTime;
    await task.FireAsync(EngineTaskTrigger.Start);

    clock.Advance(TimeSpan.FromSeconds(5));
    task.RunDuration.Should().Be(TimeSpan.FromSeconds(5), "a running task measures until now");
    gate.SetResult();
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    clock.Advance(TimeSpan.FromSeconds(5));

    task.RunStartedAtUtc.Should().Be(started);
    task.RunEndedAtUtc.Should().Be(started + TimeSpan.FromSeconds(5));
    task.RunDuration.Should().Be(TimeSpan.FromSeconds(5), "a finished task keeps its duration");
  }

  /// <summary>
  /// Verifies that a task that runs again after a recovery (Running, Recovering, Running, without a reset) does not
  /// keep the end time of its failed run: while it runs, it has no end time and measures from its new start.
  /// </summary>
  [Fact]
  public async Task EngineTask_RunsAgainAfterRecovery_ClearsTheEndOfTheFailedRun()
  {
    var ct = TestContext.Current.CancellationToken;
    var clock = new ManualTimeProvider();
    var recovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var runs = 0;
    var task = NewTask("Load", _ => Interlocked.Increment(ref runs) == 1 ? throw new InvalidOperationException("Fails.") : gate.Task, clock)
      .SetRecovery(new CustomRestarter((_, _) => recovery.Task));
    await task.FireAsync(EngineTaskTrigger.Start);
    await WaitUntilAsync(() => task.State == EngineTaskState.Recovering, "the failure must start the recovery");

    clock.Advance(TimeSpan.FromSeconds(5));
    var restarted = clock.GetUtcNow().UtcDateTime;
    recovery.SetResult();
    await WaitUntilAsync(() => task.State == EngineTaskState.Running && Volatile.Read(ref runs) == 2,
      "the recovery runs the work again");
    clock.Advance(TimeSpan.FromSeconds(3));

    task.RunStartedAtUtc.Should().Be(restarted);
    task.RunEndedAtUtc.Should().BeNull("the task is running again");
    task.RunDuration.Should().Be(TimeSpan.FromSeconds(3), "a running task measures from its latest start until now");

    gate.SetResult();
    await task.InnerTask.WaitAsync(MustComplete, ct);
    task.RunEndedAtUtc.Should().Be(restarted + TimeSpan.FromSeconds(3));
  }

  /// <summary>
  /// Verifies that an EngineTaskException ends a task without a recovery policy in Cancelled.
  /// </summary>
  [Fact]
  public async Task EngineTask_WorkThrowsEngineTaskException_WithoutRecovery_EndsInCancelled()
  {
    EngineTask task = null;
    task = NewTask("Load", _ => throw new TestTaskException(task));

    await RunToEndAsync(task);

    task.State.Should().Be(EngineTaskState.Cancelled);
  }

  /// <summary>
  /// Pins the current behaviour (see the TODO in EngineTask.ExecuteWorkAsync): an EngineTaskException maps to
  /// cancellation, and a task with a recovery policy whose token is still live recovers from that cancellation, so
  /// its work runs again.
  /// </summary>
  [Fact]
  public async Task EngineTask_WorkThrowsEngineTaskException_WithRecovery_RunsTheWorkAgain()
  {
    var runs = 0;
    EngineTask task = null;
    task = NewTask("Load", _ => ++runs == 1 ? throw new TestTaskException(task) : Task.CompletedTask)
      .SetRecovery(NoWait(maxRestarts: 5));

    await RunToEndAsync(task);

    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
    runs.Should().Be(2);
  }

  /// <summary>
  /// Verifies that a dependency completing late is ignored in every state where it can arrive (Running, Recovering,
  /// Failed), instead of throwing or changing the state. Release builds crashed on this before the fix.
  /// </summary>
  [Fact]
  public async Task EngineTask_LateCompleteDependency_InRunningRecoveringAndFailed_IsIgnored()
  {
    var ct = TestContext.Current.CancellationToken;

    var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var runningTask = NewTask("Running", _ => running.Task);
    await runningTask.FireAsync(EngineTaskTrigger.Start);
    await runningTask.FireAsync(EngineTaskTrigger.CompleteDependency);
    runningTask.State.Should().Be(EngineTaskState.Running);
    running.SetResult();
    await runningTask.InnerTask.WaitAsync(MustComplete, ct);

    var recovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var recoveringTask = NewTask("Recovering", _ => throw new InvalidOperationException("Fails."))
      .SetRecovery(new CustomRestarter((_, _) => recovering.Task));
    await recoveringTask.FireAsync(EngineTaskTrigger.Start);
    await WaitUntilAsync(() => recoveringTask.State == EngineTaskState.Recovering, "the failure must start the recovery");
    await recoveringTask.FireAsync(EngineTaskTrigger.CompleteDependency);
    recoveringTask.State.Should().Be(EngineTaskState.Recovering);
    recovering.SetException(new InvalidOperationException("Recovery fails."));
    await recoveringTask.InnerTask.WaitAsync(MustComplete, ct);

    var failedTask = NewTask("Failed", _ => throw new InvalidOperationException("Fails."));
    await RunToEndAsync(failedTask);
    await failedTask.FireAsync(EngineTaskTrigger.CompleteDependency);
    failedTask.State.Should().Be(EngineTaskState.Failed);
  }

  /// <summary>
  /// Verifies that a recovering task tells why in its state message: its failure reason is only kept for a task that
  /// ended failed, so without this the error would only be in the log.
  /// </summary>
  [Fact]
  public async Task EngineTask_Recovering_StateMessageTellsTheError()
  {
    var recovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var runs = 0;
    var task = NewTask("Load", _ => Interlocked.Increment(ref runs) == 1 ? throw new InvalidOperationException("The database is not reachable.") : Task.CompletedTask)
      .SetRecovery(new CustomRestarter((_, _) => recovery.Task));
    task.StateMessage = "Loading.";

    await task.FireAsync(EngineTaskTrigger.Start);
    // Stateless sets the state before it runs the entry action that sets the message: wait for both, or under load the
    // message is read in between.
    await WaitUntilAsync(() => task.State == EngineTaskState.Recovering && task.StateMessage != "Loading.",
      "the failure must start the recovery");

    task.StateMessage.Should().Be("Recovering after an error: The database is not reachable.");
    task.IsFailed.Should().BeFalse();
    recovery.SetResult();
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
  }

  /// <summary>
  /// Verifies that a reset while the task waits out its recovery backoff leaves the task Created: the reset cancels
  /// the wait, and the old recovery must not fire its cancellation into the next run. Started again, the task runs
  /// normally.
  /// </summary>
  [Fact]
  public async Task EngineTask_ResetDuringRecoveryBackoff_OldRecoveryLeavesTheNextRunAlone()
  {
    var ct = TestContext.Current.CancellationToken;
    var clock = new ManualTimeProvider();
    var runs = 0;
    var task = NewTask("Load", _ => Interlocked.Increment(ref runs) == 1 ? throw new InvalidOperationException("First run fails.") : Task.CompletedTask, clock)
      .SetRecovery(new RestartPolicy
      {
        MaxRestartsInWindow = 5,
        Window = TimeSpan.FromMinutes(10),
        InitialBackoff = TimeSpan.FromMinutes(1),
        MaxBackoff = TimeSpan.FromMinutes(1),
      });
    await task.FireAsync(EngineTaskTrigger.Start);
    await WaitUntilAsync(() => clock.WaitingTimers == 1, "the recovery must wait for its backoff");

    await task.FireAsync(EngineTaskTrigger.Reset);
    await Task.Delay(200, ct);

    task.State.Should().Be(EngineTaskState.Created, "the old recovery must not cancel the next run");
    task.FailReason.Should().BeNull();
    await RunToEndAsync(task);
    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
    Volatile.Read(ref runs).Should().Be(2);
  }

  /// <summary>
  /// Verifies that a recovery that ends after a reset, and was not cancelled by it (it ignores its token), does not
  /// fire its outcome into the next run either: the next run, already running, completes on its own work.
  /// </summary>
  [Fact]
  public async Task EngineTask_RecoveryEndsAfterReset_DoesNotTouchTheNextRun()
  {
    var ct = TestContext.Current.CancellationToken;
    var recovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var nextRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var runs = 0;
    var task = NewTask("Load", _ => Interlocked.Increment(ref runs) == 1 ? throw new InvalidOperationException("First run fails.") : nextRun.Task)
      .SetRecovery(new CustomRestarter((_, _) => recovery.Task));
    await task.FireAsync(EngineTaskTrigger.Start);
    await WaitUntilAsync(() => task.State == EngineTaskState.Recovering, "the failure must start the recovery");
    await task.FireAsync(EngineTaskTrigger.Reset);
    await task.FireAsync(EngineTaskTrigger.Start);
    task.State.Should().Be(EngineTaskState.Running);

    recovery.SetException(new OperationCanceledException());
    await Task.Delay(200, ct);

    task.State.Should().Be(EngineTaskState.Running, "the next run is still working");
    nextRun.SetResult();
    await task.InnerTask.WaitAsync(MustComplete, ct);
    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
  }

  /// <summary>
  /// Verifies that resetting a completed task returns it to Created with a fresh completion, so a caller waiting
  /// after the reset waits for the next run instead of seeing the previous one.
  /// </summary>
  [Fact]
  public async Task EngineTask_Reset_AfterSuccess_NewWaitersSeeTheNextRun()
  {
    var runs = 0;
    var nextRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var task = NewTask("Load", _ => ++runs == 1 ? Task.CompletedTask : nextRun.Task);
    await RunToEndAsync(task);
    var firstCompletion = task.InnerTask;

    await task.FireAsync(EngineTaskTrigger.Reset);

    task.State.Should().Be(EngineTaskState.Created);
    task.InnerTask.Should().NotBeSameAs(firstCompletion);
    task.InnerTask.IsCompleted.Should().BeFalse();
    await task.FireAsync(EngineTaskTrigger.Start);
    task.InnerTask.IsCompleted.Should().BeFalse("the next run has not finished");
    nextRun.SetResult();
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
    runs.Should().Be(2);
  }
}
