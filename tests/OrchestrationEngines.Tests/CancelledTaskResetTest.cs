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
/// Contains tests for a task that ended Cancelled through something else than a stop (an OperationCanceledException
/// that is not the engine's, such as a driver time-out): a later reset must run it again, and a task that depends on it
/// must not wait for it forever.
/// </summary>
public class CancelledTaskResetTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// An engine with a load whose first run is cancelled from outside, and an index that depends on the load.
  /// </summary>
  private sealed class LoadAndIndexEngine() : BaseEngine(NullLogger.Instance)
  {
    private int _loads;

    public int Loads => Volatile.Read(ref _loads);

    public EngineTask Load => Tasks["Load"];

    public EngineTask Index => Tasks["Index"];

    protected override void SetUpTasks()
    {
      var load = CreateTask("Load", _ => Interlocked.Increment(ref _loads) == 1
        ? throw new TaskCanceledException("The command timed out.")
        : Task.CompletedTask);
      CreateTask("Index", _ => Task.CompletedTask).AddDependsOn(load);
    }
  }

  private static async Task<LoadAndIndexEngine> CancelledEngineAsync()
  {
    var engine = new LoadAndIndexEngine { Context = new NoContext() };
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    engine.Load.State.Should().Be(EngineTaskState.Cancelled, "the load was cancelled from outside");
    engine.Load.FailReason.Should().Be("Cancelled because the work was cancelled (not by a stop of the engine).");
    engine.Index.State.Should().Be(EngineTaskState.DependencyFailed);
    engine.Index.FailReason.Should().Be("Cancelled because 'Load', which it depends on, was cancelled.",
      "the reason must be known when the load enters Failed, before its Cancelled substate");
    engine.IsCancellationRequested.Should().BeFalse("the engine itself was not stopped");
    return engine;
  }

  /// <summary>
  /// Verifies that resetting a task that was cancelled from outside, with the task that depends on it, runs both again,
  /// and the engine becomes Ready.
  /// </summary>
  [Fact]
  public async Task BaseEngine_TriggerResetAsync_TaskCancelledFromOutside_RunsAgain()
  {
    var engine = await CancelledEngineAsync();

    await engine.TriggerResetAsync([engine.Load, engine.Index]);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    engine.Loads.Should().Be(2);
    engine.Index.State.Should().Be(EngineTaskState.CompletedWithSuccess);
    engine.State.Should().Be(EngineState.Ready);
  }

  /// <summary>
  /// Verifies that a task reset without the task it depends on, which stays cancelled, fails at once instead of waiting
  /// forever, so waiting for the engine ends.
  /// </summary>
  [Fact]
  public async Task BaseEngine_TriggerResetAsync_DependencyStaysCancelled_DependentFailsInsteadOfWaiting()
  {
    var engine = await CancelledEngineAsync();

    await engine.TriggerResetAsync([engine.Index]);
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    engine.Index.State.Should().Be(EngineTaskState.DependencyFailed);
    engine.Index.FailReason.Should().Be("Cancelled because 'Load', which it depends on, was cancelled.");
    engine.Index.WasCancelled.Should().BeTrue();
    engine.Loads.Should().Be(1);
  }

  /// <summary>
  /// Verifies that a stopped engine's cancelled tasks are not reset: a stop is final.
  /// </summary>
  [Fact]
  public async Task BaseEngine_TriggerResetAsync_StoppedEngine_LeavesCancelledTaskCancelled()
  {
    var engine = await CancelledEngineAsync();
    await engine.StopAsync();

    await engine.TriggerResetAsync([engine.Load, engine.Index]);

    engine.Load.State.Should().Be(EngineTaskState.Cancelled);
    engine.Loads.Should().Be(1);
  }
}
