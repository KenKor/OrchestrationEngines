using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Mechanic;
using OrchestrationEngines.Restarter;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for waiting until one named task has completed with success, on an engine and on the coordinator.
/// </summary>
public class EnsureTaskReadyTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

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
        .SetRecovery(new RestartPolicy
        {
          MaxRestartsInWindow = 1,
          Window = TimeSpan.FromMinutes(10),
          InitialBackoff = TimeSpan.Zero,
          MaxBackoff = TimeSpan.Zero,
        });
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

  private static async Task<RerunEngine> WaitingOnTheFirstRunAsync(CancellationToken ct)
  {
    var engine = new RerunEngine { Context = new NoContext() };
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    return engine;
  }

  private static async Task<MixedEngine> StartedEngineAsync(CancellationToken ct)
  {
    var engine = new MixedEngine { Context = new NoContext() };
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    return engine;
  }

  /// <summary>
  /// Verifies that waiting for a task that completes with success returns.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTaskReadyAsync_TaskCompletes_Returns()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = await StartedEngineAsync(ct);

    await engine.EnsureTaskReadyAsync("load").WaitAsync(MustComplete, ct);

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
    try
    {
      await engine.StartAsync().WaitAsync(MustComplete, ct);
    }
    catch (InvalidOperationException)
    {
      // How a failed set-up surfaces from StartAsync is not what this test is about.
    }

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
      .SetRecovery(new RestartPolicy
      {
        MaxRestartsInWindow = 2,
        Window = TimeSpan.FromMinutes(10),
        InitialBackoff = TimeSpan.Zero,
        MaxBackoff = TimeSpan.Zero,
      });

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
    await engine.EnsureReadyAsync("Broken").WaitAsync(MustComplete, ct);
    var broken = engine.Tasks["Broken"];
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

  private static EngineCoordinator CoordinatorWith(Func<CancellationToken, Task<IEngine>> create,
    Func<CancellationToken, Task<IEngineContext>> contextFactory = null)
  {
    var coordinator = new EngineCoordinator(nameof(EnsureTaskReadyTest), NullLogger.Instance, useDispatcher: false);
    coordinator.RegisterEngine("Mixed", create, requiredForRunning: false)
      .SetContextFactory(contextFactory ?? (_ => Task.FromResult<IEngineContext>(new NoContext())));
    return coordinator;
  }

  private static EngineCoordinator CoordinatorWith(IEngine engine,
    Func<CancellationToken, Task<IEngineContext>> contextFactory = null) =>
    CoordinatorWith(_ => Task.FromResult(engine), contextFactory);

  /// <summary>
  /// Verifies that the coordinator, not started yet, starts and waits for an engine's task that completes.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureEngineTaskReadyAsync_TaskCompletes_Returns()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new MixedEngine();
    var coordinator = CoordinatorWith(engine);

    await coordinator.EnsureEngineTaskReadyAsync("mixed", "Load", ct).WaitAsync(MustComplete, ct);

    engine.Tasks["Load"].State.Should().Be(EngineTaskState.CompletedWithSuccess);
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that the coordinator passes on the exception of an engine's task that fails.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureEngineTaskReadyAsync_TaskFails_Throws()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CoordinatorWith(new MixedEngine());

    var thrown = await coordinator.Awaiting(c => c.EnsureEngineTaskReadyAsync("Mixed", "Broken", ct).WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<EngineTaskNotReadyException>();

    thrown.Which.TaskName.Should().Be("Broken");
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that the coordinator reports an engine that could not be created by its creation task, which bears the
  /// engine's name.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureEngineTaskReadyAsync_EngineNotCreated_ThrowsForTheEngine()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CoordinatorWith(_ => throw new InvalidOperationException("No connection."));

    var thrown = await coordinator.Awaiting(c => c.EnsureEngineTaskReadyAsync("Mixed", "Load", ct).WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<EngineTaskNotReadyException>();

    thrown.Which.TaskName.Should().BeEquivalentTo("Mixed");
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that the coordinator reports an engine that was created but never started (its context failed) by its
  /// kick-start task, not as an unknown task.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureEngineTaskReadyAsync_EngineNotStarted_ThrowsForTheKickStart()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CoordinatorWith(new MixedEngine(), _ => throw new InvalidOperationException("No context."));

    var thrown = await coordinator.Awaiting(c => c.EnsureEngineTaskReadyAsync("Mixed", "Load", ct).WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<EngineTaskNotReadyException>();

    thrown.Which.TaskName.Should().Be("Mixed" + EngineKickStartTask.PostFix);
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// An engine whose one required task fails, so it starts and faults.
  /// </summary>
  private sealed class FaultingEngine() : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Required", _ => throw new LoadException("Broken."));
  }

  /// <summary>
  /// Verifies that the coordinator reports an unknown task on an engine that started and faulted as an unknown task,
  /// not as the engine's failure.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureEngineTaskReadyAsync_UnknownTaskOnAFaultedEngine_ThrowsArgumentException()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CoordinatorWith(new FaultingEngine());

    var thrown = await coordinator.Awaiting(c => c.EnsureEngineTaskReadyAsync("Mixed", "Typo", ct).WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<ArgumentException>();

    thrown.Which.ParamName.Should().Be("taskName");
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that the coordinator throws an ArgumentException for an engine that is not registered.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureEngineTaskReadyAsync_UnknownEngine_ThrowsArgumentException()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CoordinatorWith(new MixedEngine());

    var thrown = await coordinator.Awaiting(c => c.EnsureEngineTaskReadyAsync("Missing", "Load", ct).WaitAsync(MustComplete, ct))
      .Should().ThrowAsync<ArgumentException>();

    thrown.Which.ParamName.Should().Be("engineName");
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }
}
