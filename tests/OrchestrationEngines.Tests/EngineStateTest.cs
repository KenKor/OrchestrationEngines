using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the state an engine reports from its tasks, and for the coordinator's state when an engine's
/// context is refreshed.
/// </summary>
public class EngineStateTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);
  private const string Name = "E";

  /// <summary>
  /// An engine with independent required tasks that end as the test says: cancelled, failed, or completed.
  /// </summary>
  private sealed class EndingEngine(params string[] endings) : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks()
    {
      for (var i = 0; i < endings.Length; i++)
      {
        var ending = endings[i];
        CreateTask($"{ending}{i}", _ => ending switch
        {
          "Cancelled" => Task.FromException(new OperationCanceledException()),
          "Failed" => Task.FromException(new InvalidOperationException("The task failed.")),
          _ => Task.CompletedTask,
        });
      }
    }
  }

  /// <summary>
  /// An engine with one task that completes at once.
  /// </summary>
  private sealed class QuickEngine() : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Load", _ => Task.CompletedTask);
  }

  /// <summary>
  /// An engine with a required load and a background monitor (not required for running) that runs until released.
  /// </summary>
  private sealed class MonitoredEngine(Task monitor) : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks()
    {
      var load = CreateTask("Load", _ => Task.CompletedTask);
      CreateTask("Monitor", _ => monitor, requiredForRunning: false).AddDependsOn(load);
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
  /// Verifies that an engine with a cancelled and a failed task reports Faulted, the more severe state, also when the
  /// cancelled task comes first.
  /// </summary>
  [Fact]
  public async Task BaseEngine_State_CancelledAndFailedTask_IsFaulted()
  {
    var engine = new EndingEngine("Cancelled", "Failed");

    await engine.StartAsync();

    await WaitUntilAsync(() => engine.Tasks.Values.All(t => t.IsFailed), "both tasks must end");
    await WaitUntilAsync(() => engine.State == EngineState.Faulted, "a failed task makes the engine Faulted");
  }

  /// <summary>
  /// Verifies that an engine whose only unsuccessful task was cancelled reports Stopped.
  /// </summary>
  [Fact]
  public async Task BaseEngine_State_CancelledTaskOnly_IsStopped()
  {
    var engine = new EndingEngine("Cancelled", "Completed");

    await engine.StartAsync();

    await WaitUntilAsync(() => engine.Tasks.Values.All(t => t.IsCompleted), "both tasks must end");
    await WaitUntilAsync(() => engine.State == EngineState.Stopped, "a cancelled task stops the engine");
  }

  /// <summary>
  /// Verifies that an engine is Ready while a background task (not required for running) still runs.
  /// </summary>
  [Fact]
  public async Task BaseEngine_State_BackgroundTaskStillRunning_IsReady()
  {
    var monitor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = new MonitoredEngine(monitor.Task) { Context = new NoContext() };

    await engine.StartAsync();

    await WaitUntilAsync(() => engine.State == EngineState.Ready, "the background task must not hold up readiness");
    engine.Tasks["Monitor"].IsCompleted.Should().BeFalse();
    monitor.SetResult();
  }

  /// <summary>
  /// Verifies that waiting for all tasks does not wait for a background task that keeps running (a monitor), which
  /// would never end.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureAllReadyAsync_BackgroundTaskStillRunning_DoesNotWaitForIt()
  {
    var monitor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = new MonitoredEngine(monitor.Task) { Context = new NoContext() };
    await engine.StartAsync();

    await engine.EnsureAllReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    engine.Tasks["Load"].IsCompletedWithSuccess.Should().BeTrue();
    await WaitUntilAsync(() => engine.Tasks["Monitor"].State == EngineTaskState.Running, "the monitor must have been started");
    monitor.SetResult();
  }

  /// <summary>
  /// Verifies that an engine whose tasks all completed, but that never got a context, reports Stopped, not Ready.
  /// </summary>
  [Fact]
  public async Task BaseEngine_State_NoContext_IsStopped()
  {
    var engine = new EndingEngine("Completed");

    await engine.StartAsync();

    await WaitUntilAsync(() => engine.State == EngineState.Stopped, "an engine without a context is not Ready");
  }

  /// <summary>
  /// Verifies that refreshing the context of an engine in a running coordinator passes through Initializing (the
  /// RefreshEngine trigger) and returns to Running, since a refresh does not change the mechanic's state.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RefreshEngineContextAsync_RunningCoordinator_ReturnsToRunning()
  {
    var logger = new FakeLogger();
    var engine = new QuickEngine();
    var coordinator = new EngineCoordinator(nameof(EngineStateTest), logger, useDispatcher: false);
    coordinator.RegisterEngineDescriptor(new EngineDescriptor(Name, _ => Task.FromResult<IEngine>(engine), requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext())));
    await coordinator.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => coordinator.State == EngineCoordinatorState.Running, "the coordinator must be running");
    var freshContext = new NoContext();

    await coordinator.RefreshEngineContextAsync(Name, freshContext, TestContext.Current.CancellationToken);

    engine.Context.Should().BeSameAs(freshContext);
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
    var transitions = logger.Collector.GetSnapshot().Select(r => r.Message).ToArray();
    transitions.Should().Contain(m => m.Contains("Running --RefreshEngine--> Initializing"));
    transitions.Should().Contain(m => m.Contains("Initializing --EngineReady--> Running"));
  }

  /// <summary>
  /// Verifies that a refresh for an engine the coordinator does not know throws, and still leaves the coordinator
  /// Running instead of stuck in Initializing.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RefreshEngineContextAsync_UnknownEngine_ThrowsAndStaysRunning()
  {
    var engine = new QuickEngine();
    var coordinator = new EngineCoordinator(nameof(EngineStateTest), NullLogger.Instance, useDispatcher: false);
    coordinator.RegisterEngineDescriptor(new EngineDescriptor(Name, _ => Task.FromResult<IEngine>(engine), requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext())));
    await coordinator.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => coordinator.State == EngineCoordinatorState.Running, "the coordinator must be running");

    var refresh = () => coordinator.RefreshEngineContextAsync("Unknown", new NoContext(), TestContext.Current.CancellationToken);

    await refresh.Should().ThrowAsync<ArgumentException>();
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }
}
