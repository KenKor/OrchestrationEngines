using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Restarter;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the readiness calls a host makes on an engine (waiting for the engine or for some of its tasks),
/// for adding tasks, and for the levels at which failures that are not the end of the story are logged.
/// </summary>
public class EngineReadinessApiTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// An engine with two load tasks that wait for a gate, one created with asynchronous and one with synchronous work.
  /// </summary>
  private sealed class GatedEngine(Task gate, ILogger logger = null) : BaseEngine(logger ?? NullLogger.Instance)
  {
    public int SyncRuns;

    protected override void SetUpTasks()
    {
      CreateTask("Load", async token => await gate.WaitAsync(token).ConfigureAwait(false));
      CreateTask("Index", token =>
      {
        gate.Wait(token);
        Interlocked.Increment(ref SyncRuns);
      });
    }

    public EngineTask AddLateTask(string name) =>
      AddTask(name, new EngineTask(name, _ => Task.CompletedTask, requiredForRunning: false, NullLogger.Instance,
        CancellationToken.None));
  }

  /// <summary>
  /// Verifies that the blocking EnsureReadyWait returns once the engine is Ready, and that synchronous work ran.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureReadyWait_ReturnsOnceReady()
  {
    var ct = TestContext.Current.CancellationToken;
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    // A context, as the coordinator gives every engine: without one a loaded engine is not Ready.
    var engine = new GatedEngine(gate.Task) { Context = new NoContext() };
    await engine.StartAsync().WaitAsync(MustComplete, ct);

    var waiting = Task.Run(engine.EnsureReadyWait, ct);
    await Task.Delay(100, ct);
    waiting.IsCompleted.Should().BeFalse("the tasks wait for the gate");
    gate.SetResult();
    await waiting.WaitAsync(MustComplete, ct);

    engine.State.Should().Be(EngineState.Ready);
    engine.SyncRuns.Should().Be(1);
  }

  /// <summary>
  /// Verifies that waiting for named tasks of which one does not exist (an optional task the host did not configure)
  /// waits for the task that does, and then reports success.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTasksReadyAsync_OneNameMissing_WaitsForTheOthers()
  {
    var ct = TestContext.Current.CancellationToken;
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var logger = new FakeLogger();
    logger.ControlLevel(LogLevel.Trace, true);
    var engine = new GatedEngine(gate.Task, logger);
    await engine.StartAsync().WaitAsync(MustComplete, ct);

    var waiting = engine.EnsureTasksReadyAsync(["Load", "Missing"]);
    await Task.Delay(100, ct);
    waiting.IsCompleted.Should().BeFalse("Load waits for the gate");
    gate.SetResult();

    (await waiting.WaitAsync(MustComplete, ct)).Should().BeTrue();
    engine.Tasks["Load"].State.Should().Be(EngineTaskState.CompletedWithSuccess);
    logger.Collector.GetSnapshot().Should().Contain(r => r.Level == LogLevel.Trace && r.Message.Contains("Some of the task(s)"));
  }

  /// <summary>
  /// Verifies that waiting for tasks that have already completed reports success at once: they exist, there is just
  /// nothing left to wait for.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTasksReadyAsync_TasksAlreadyCompleted_ReturnsTrue()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new GatedEngine(Task.CompletedTask) { Context = new NoContext() };
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    (await engine.EnsureTasksReadyAsync(["Load"]).WaitAsync(MustComplete, ct)).Should().BeTrue();
    (await engine.EnsureTasksReadyAsync(["Load", "Index"]).WaitAsync(MustComplete, ct)).Should().BeTrue();
  }

  /// <summary>
  /// Verifies that waiting for tasks that do not exist reports failure with a warning, worded for one or more names.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureTasksReadyAsync_NoneFound_ReturnsFalseWithAWarning()
  {
    var ct = TestContext.Current.CancellationToken;
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var logger = new FakeLogger();
    var engine = new GatedEngine(gate.Task, logger);
    await engine.StartAsync().WaitAsync(MustComplete, ct);

    try
    {
      (await engine.EnsureTasksReadyAsync(["Missing"]).WaitAsync(MustComplete, ct)).Should().BeFalse();
      (await engine.EnsureTasksReadyAsync(["Missing", "Absent"]).WaitAsync(MustComplete, ct)).Should().BeFalse();

      var warnings = logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Warning).Select(r => r.Message).ToList();
      warnings.Should().Contain(m => m.Contains("Task 'Missing' not found"));
      warnings.Should().Contain(m => m.Contains("None of the task(s) 'Missing', 'Absent' found"));
    }
    finally
    {
      gate.TrySetResult();
    }
  }

  /// <summary>
  /// Verifies that a task added after the engine's set-up is added to the published task set.
  /// </summary>
  [Fact]
  public async Task BaseEngine_AddTask_AfterSetUp_PublishesTheTask()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new GatedEngine(Task.CompletedTask);
    await engine.StartAsync().WaitAsync(MustComplete, ct);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    var late = engine.AddLateTask("Late");

    engine.Tasks.Should().ContainKey("Late").WhoseValue.Should().BeSameAs(late);
    engine.Tasks.Should().ContainKeys("Load", "Index");
  }

  /// <summary>
  /// Verifies that a trigger the task's state does not handle is logged as a warning and ignored, not thrown.
  /// </summary>
  [Fact]
  public async Task EngineTask_FireAsync_UnhandledTrigger_LogsAWarning()
  {
    var logger = new FakeLogger();
    var task = new EngineTask("Load", _ => Task.CompletedTask, requiredForRunning: true, logger, CancellationToken.None);

    await task.FireAsync(EngineTaskTrigger.SucceedRecovery);

    task.State.Should().Be(EngineTaskState.Created);
    logger.Collector.GetSnapshot().Should().Contain(r =>
      r.Level == LogLevel.Warning && r.Message.Contains("Unhandled trigger SucceedRecovery in state Created"));
  }

  /// <summary>
  /// Verifies that work that timed out, ignored its cancellation and then threw only has its exception logged at
  /// Debug: the task had ended in TimedOut already.
  /// </summary>
  [Fact]
  public async Task EngineTask_TimedOutWorkThrowsLater_LogsAtDebug()
  {
    var ct = TestContext.Current.CancellationToken;
    var clock = new ManualTimeProvider();
    var logger = new FakeLogger();
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var task = new EngineTask("Load", async _ =>
    {
      await release.Task.ConfigureAwait(false);
      throw new InvalidOperationException("Too late.");
    }, requiredForRunning: true, logger, CancellationToken.None, clock).SetTimeOut(TimeSpan.FromMinutes(1));

    try
    {
      await task.FireAsync(EngineTaskTrigger.Start);
      var deadline = DateTime.UtcNow + MustComplete;
      while (clock.WaitingTimers == 0 && DateTime.UtcNow < deadline)
        await Task.Delay(10, ct);
      clock.Advance(TimeSpan.FromMinutes(1));
      await task.InnerTask.WaitAsync(MustComplete, ct);
      task.State.Should().Be(EngineTaskState.TimedOut);

      release.SetResult();
      deadline = DateTime.UtcNow + MustComplete;
      while (!logger.Collector.GetSnapshot().Any(r => r.Exception != null && r.Level == LogLevel.Debug) && DateTime.UtcNow < deadline)
        await Task.Delay(10, ct);

      logger.Collector.GetSnapshot().Should().Contain(r =>
        r.Level == LogLevel.Debug && r.Message.Contains("ended with an exception after its run had ended"));
      logger.Collector.GetSnapshot().Should().NotContain(r => r.Level == LogLevel.Error && r.Message.Contains("Too late."));
    }
    finally
    {
      release.TrySetResult();
    }
  }

  // The kick-start's own exception type is private: recognise it by its name.
  private static bool IsNotStarted(Exception exception) => exception?.GetType().Name == "EngineNotStartedException";

  /// <summary>
  /// An engine whose first load fails and whose later loads succeed.
  /// </summary>
  private sealed class FailsOnceEngine() : BaseEngine(NullLogger.Instance)
  {
    private int _loads;

    protected override void SetUpTasks() => CreateTask("Load", _ =>
      Interlocked.Increment(ref _loads) == 1 ? throw new InvalidOperationException("Not reachable.") : Task.CompletedTask);
  }

  /// <summary>
  /// Verifies that an engine that failed to start is logged as a warning when the restart policy may start it again,
  /// and as an error when nothing will.
  /// </summary>
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task EngineKickStartTask_EngineNotStarted_LogsAWarningOnlyWhenItMayRestart(bool withRestartPolicy)
  {
    var ct = TestContext.Current.CancellationToken;
    var logger = new FakeLogger();
    var coordinator = new EngineCoordinator(nameof(EngineReadinessApiTest), logger, useDispatcher: false);
    var descriptor = coordinator.RegisterEngine("E", _ => Task.FromResult<IEngine>(new FailsOnceEngine()));
    if (withRestartPolicy)
      descriptor.SetRecoveryHelper(new RestartPolicy
      {
        MaxRestartsInWindow = 3,
        Window = TimeSpan.FromMinutes(10),
        InitialBackoff = TimeSpan.Zero,
        MaxBackoff = TimeSpan.Zero,
      });

    try
    {
      await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

      var records = logger.Collector.GetSnapshot();
      if (withRestartPolicy)
      {
        records.Should().Contain(r => r.Level == LogLevel.Warning && r.Message.Contains("The restart policy decides"));
        records.Should().NotContain(r => r.Level == LogLevel.Error && IsNotStarted(r.Exception));
        coordinator.GetEngineIfReady("E").Should().NotBeNull("the restart started it again");
      }
      else
      {
        records.Should().Contain(r => r.Level == LogLevel.Error && IsNotStarted(r.Exception));
        records.Should().NotContain(r => r.Message.Contains("The restart policy decides"));
      }
    }
    finally
    {
      await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
    }
  }
}
