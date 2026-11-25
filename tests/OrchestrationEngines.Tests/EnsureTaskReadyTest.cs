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
/// Contains tests for waiting until one named task of an engine has completed with success.
/// </summary>
public class EnsureTaskReadyTest
{
  /// <summary>
  /// The exception the failing task's work throws.
  /// </summary>
  private sealed class LoadException(string message) : Exception(message);

  /// <summary>
  /// An engine with a task that completes, one that fails, one that is cancelled and one that depends on the failing
  /// one. None is required for running, so the engine itself does not fault.
  /// </summary>
  private sealed class MixedEngine() : BaseEngine(NullLogger.Instance)
  {
    public static readonly LoadException Thrown = new("The file is missing.");

    protected override void SetUpTasks()
    {
      CreateTask("Load", _ => Task.CompletedTask, requiredForRunning: false);
      var broken = CreateTask("Broken", _ => throw Thrown, requiredForRunning: false);
      CreateTask("Cancelled", _ => throw new OperationCanceledException("The query timed out."), requiredForRunning: false);
      CreateTask("AfterBroken", _ => Task.CompletedTask, requiredForRunning: false).AddDependsOn(broken);
      CreateTask("Retried", _ => throw new LoadException("Still missing."), requiredForRunning: false)
        .SetRecovery(NoWait(1));
      CreateTask("Slow", token => Task.Delay(Timeout.Infinite, token), requiredForRunning: false)
        .SetTimeOut(TimeSpan.FromMilliseconds(50));
    }
  }

  /// <summary>
  /// An engine with one required task, "Load", whose first run waits until it is cancelled and whose later runs
  /// complete. Its set-up can be made to fail, for the next start.
  /// </summary>
  private sealed class RerunEngine() : BaseEngine(NullLogger.Instance)
  {
    private int _runs;

    public TaskCompletionSource FirstRunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool FailSetUp { get; set; }

    public int Runs => Volatile.Read(ref _runs);

    protected override void SetUpTasks()
    {
      if (FailSetUp)
        throw new InvalidOperationException("The set-up failed.");
      CreateTask("Load", token =>
      {
        if (Interlocked.Increment(ref _runs) > 1)
          return Task.CompletedTask;
        FirstRunStarted.TrySetResult();
        return Task.Delay(Timeout.Infinite, token);
      });
    }
  }

  private static async Task<TEngine> StartedEngineAsync<TEngine>(TEngine engine, CancellationToken ct)
    where TEngine : BaseEngine
  {
    engine.Context = new NoContext();
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    return engine;
  }

  private static Task<MixedEngine> StartedEngineAsync(CancellationToken ct) => StartedEngineAsync(new MixedEngine(), ct);

  private static Task<RerunEngine> WaitingOnTheFirstRunAsync(CancellationToken ct) =>
    StartedEngineAsync(new RerunEngine(), ct);

  /// <summary>
  /// Verifies that waiting for a task that completes with success returns.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_TaskCompletes_Returns()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);

    await engine.EnsureTaskReadyAsync("load", ct).WaitAsync(MustComplete, ct);

    engine.Tasks["Load"].State.Should().Be(EngineTaskState.CompletedWithSuccess);
  }

  /// <summary>
  /// Verifies that waiting for a task whose work throws reports the task, its state, its reason and the exception.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_TaskFails_ThrowsWithItsReasonAndException()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);

    var thrown = await engine.Awaiting(e => e.EnsureTaskReadyAsync("Broken").WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<EngineTaskNotReadyException>();

    thrown.Which.TaskName.Should().Be("Broken");
    thrown.Which.State.Should().Be(EngineTaskState.Failed);
    thrown.Which.FailReason.Should().Be("The file is missing.");
    thrown.Which.InnerException.Should().BeSameAs(MixedEngine.Thrown);
  }

  /// <summary>
  /// Verifies that waiting for a task that is cancelled reports its state, without an inner exception.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_TaskCancelled_ThrowsWithItsState()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);

    var thrown = await engine.Awaiting(e => e.EnsureTaskReadyAsync("Cancelled").WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<EngineTaskNotReadyException>();

    thrown.Which.TaskName.Should().Be("Cancelled");
    thrown.Which.State.Should().Be(EngineTaskState.Cancelled);
    thrown.Which.InnerException.Should().BeNull();
  }

  /// <summary>
  /// Verifies that waiting for a task whose dependency failed reports DependencyFailed, without the dependency's
  /// exception.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_DependencyFails_ThrowsDependencyFailed()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);

    var thrown = await engine.Awaiting(e => e.EnsureTaskReadyAsync("AfterBroken").WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<EngineTaskNotReadyException>();

    thrown.Which.State.Should().Be(EngineTaskState.DependencyFailed);
    thrown.Which.InnerException.Should().BeNull();
  }

  /// <summary>
  /// Verifies that a task whose recovery gives up reports RecoveryFailed with the recovery's exception (the policy's
  /// limit), not the work's.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_RecoveryFails_ThrowsWithTheRecoveryException()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);

    var thrown = await engine.Awaiting(e => e.EnsureTaskReadyAsync("Retried").WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<EngineTaskNotReadyException>();

    thrown.Which.State.Should().Be(EngineTaskState.RecoveryFailed);
    thrown.Which.InnerException.Should().BeOfType<EngineTaskLimitExceededException>();
    thrown.Which.InnerException.Should().BeSameAs(engine.Tasks["Retried"].FailException);
  }

  /// <summary>
  /// Verifies that a wait during which the engine's tasks are reset and the engine started again (a restart, which
  /// replaces the task) answers with the next run's outcome, not with the Cancelled that ended the first run.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_RestartDuringTheWait_AnswersWithTheNextRun()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await WaitingOnTheFirstRunAsync(ct);
    var wait = engine.EnsureTaskReadyAsync("Load", ct);
    await engine.FirstRunStarted.Task.WaitAsync(MustComplete, ct);

    await engine.ResetAllTasksAsync().WaitAsync(MustComplete, ct);
    await engine.StartAsync().WaitAsync(MustComplete, ct);

    await wait.WaitAsync(MustComplete, ct);
    engine.Runs.Should().Be(2);
  }

  /// <summary>
  /// Verifies that a wait during which the task is reset in place (and started again) answers with the next run's
  /// outcome.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_ResetDuringTheWait_AnswersWithTheNextRun()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await WaitingOnTheFirstRunAsync(ct);
    var wait = engine.EnsureTaskReadyAsync("Load", ct);
    await engine.FirstRunStarted.Task.WaitAsync(MustComplete, ct);

    await engine.TriggerResetAsync([engine.Tasks["Load"]]).WaitAsync(MustComplete, ct);

    await wait.WaitAsync(MustComplete, ct);
    engine.Runs.Should().Be(2);
  }

  /// <summary>
  /// Verifies that a wait whose task a restart ended, while the restart's set-up fails (so the task is gone), reports
  /// how the task it waited for ended, not an unknown name.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_RestartLosesTheTask_ThrowsForTheEndedTask()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await WaitingOnTheFirstRunAsync(ct);
    var wait = engine.EnsureTaskReadyAsync("Load", ct);
    await engine.FirstRunStarted.Task.WaitAsync(MustComplete, ct);

    await engine.ResetAllTasksAsync().WaitAsync(MustComplete, ct);
    engine.FailSetUp = true;
    var start = await Record.ExceptionAsync(() => engine.StartAsync().WaitAsync(MustComplete, ct));

    start.Should().BeOfType<InvalidOperationException>();
    var thrown = await wait.Awaiting(w => w.WaitAsync(MustComplete, ct)).Should().ThrowAsync<EngineTaskNotReadyException>();
    thrown.Which.TaskName.Should().Be("Load");
    thrown.Which.State.Should().Be(EngineTaskState.Cancelled);
  }

  /// <summary>
  /// Verifies that cancelling the token ends the wait, and the task runs on.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_TokenCancelled_EndsTheWaitOnly()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await WaitingOnTheFirstRunAsync(ct);
    using var cts = new CancellationTokenSource();
    var wait = engine.EnsureTaskReadyAsync("Load", cts.Token);
    await engine.FirstRunStarted.Task.WaitAsync(MustComplete, ct);

    cts.Cancel();

    await wait.Awaiting(w => w.WaitAsync(MustComplete, ct)).Should().ThrowAsync<OperationCanceledException>();
    engine.Tasks["Load"].State.Should().Be(EngineTaskState.Running);
  }

  /// <summary>
  /// Verifies that a task that times out reports TimedOut, without an inner exception.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_TaskTimesOut_ThrowsTimedOut()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);

    var thrown = await engine.Awaiting(e => e.EnsureTaskReadyAsync("Slow").WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<EngineTaskNotReadyException>();

    thrown.Which.State.Should().Be(EngineTaskState.TimedOut);
    thrown.Which.InnerException.Should().BeNull();
  }

  /// <summary>
  /// Verifies that a task whose work fails once and then succeeds through its recovery does not keep the exception.
  /// </summary>
  [Fact]
  public async Task EngineTask_RecoverySucceeds_ClearsFailException()
  {
    var ct = TestContext.Current.CancellationToken;
    var runs = 0;
    var task = new EngineTask("Load", _ => ++runs == 1 ? throw new LoadException("First run fails.") : Task.CompletedTask,
        requiredForRunning: true, NullLogger.Instance, CancellationToken.None)
      .SetRecovery(NoWait(2));

    await task.FireAsync(EngineTaskTrigger.Start);
    await task.InnerTask.WaitAsync(MustComplete, ct);

    task.State.Should().Be(EngineTaskState.CompletedWithSuccess);
    task.FailException.Should().BeNull();
  }

  /// <summary>
  /// Verifies that a reset clears the exception a failed task recorded.
  /// </summary>
  [Fact]
  public async Task EngineTask_Reset_ClearsFailException()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);
    var broken = engine.Tasks["Broken"];
    await engine.EnsureReadyAsync(broken, ct).WaitAsync(MustComplete, ct);
    broken.FailException.Should().BeSameAs(MixedEngine.Thrown);

    await broken.FireAsync(EngineTaskTrigger.Reset);

    broken.FailException.Should().BeNull();
  }

  /// <summary>
  /// Verifies that waiting for a task the engine does not have throws an ArgumentException naming the parameter.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_UnknownName_ThrowsArgumentException()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);

    var thrown = await engine.Awaiting(e => e.EnsureTaskReadyAsync("Missing").WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<ArgumentException>();

    thrown.Which.ParamName.Should().Be("taskName");
  }

  private static EngineCoordinator CoordinatorWith(IEngine engine,
    Func<CancellationToken, Task<IEngineContext>> contextFactory)
  {
    var coordinator = new EngineCoordinator(nameof(EnsureTaskReadyTest), NullLogger.Instance);
    coordinator.RegisterEngine("Mixed", _ => Task.FromResult(engine), requiredForRunning: false)
      .SetContextFactory(contextFactory)
      .WithoutRestarts();
    return coordinator;
  }

  /// <summary>
  /// An engine that creates its task in its constructor, outside a set-up, and counts the runs of its work.
  /// </summary>
  private sealed class ConstructedTasksEngine : BaseEngine
  {
    private int _renders;

    public ConstructedTasksEngine() : base(NullLogger.Instance) =>
      CreateTask("render", _ =>
      {
        Interlocked.Increment(ref _renders);
        return Task.CompletedTask;
      });

    public int Renders => Volatile.Read(ref _renders);

    // Not reached: the context fails before the engine starts.
    protected override void SetUpTasks()
    {
    }
  }

  /// <summary>
  /// Verifies that resetting the tasks of an engine that never started, but created a task in its constructor, is
  /// refused as for an engine that has not started, and does not run the task's work without a context.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_ResetTasksForEngineAsync_NotStartedEngineWithTasks_IsRefused()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new ConstructedTasksEngine();
    var coordinator = CoordinatorWith(engine, _ => throw new InvalidOperationException("No context."));

    var thrown = await coordinator.Awaiting(c => c.ResetTasksForEngineAsync("Mixed", ["render"], resetDependentTasks: false, ct)
        .WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<InvalidOperationException>();

    thrown.Which.Message.Should().Contain("has not started");
    engine.Renders.Should().Be(0);
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that looking up an engine whose kick-start gave up answers null, also on a second lookup.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_GetEngineAsync_KickStartGaveUpOnANotStartedEngine_ReturnsNull()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CoordinatorWith(new MixedEngine(), _ => throw new InvalidOperationException("No context."));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var first = await coordinator.GetEngineAsync("Mixed", ct).WaitAsync(MustComplete, ct);
    var second = await coordinator.GetEngineAsync("Mixed", ct).WaitAsync(MustComplete, ct);

    first.Should().BeNull();
    second.Should().BeNull();
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }
}
