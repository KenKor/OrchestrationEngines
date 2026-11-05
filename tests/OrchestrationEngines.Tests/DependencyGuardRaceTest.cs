using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the transitions of a task that depend on the state of its dependencies, while a dependency changes
/// state at the same time (under its own lock). Each firing must take one decision, from one look at the dependencies:
/// when two conditions are evaluated one after the other and the dependency changes in between, both can hold, and the
/// state machine throws.
/// </summary>
public class DependencyGuardRaceTest
{
  /// <summary>
  /// A task whose dependencies change state between two looks: not ready and not failed at the first look at whether one
  /// failed, failed from the second look on.
  /// </summary>
  private sealed class RacingTask() : EngineTask("Dependent", _ => Task.CompletedTask, true, NullLogger.Instance, CancellationToken.None)
  {
    private int _failedLooks;

    protected override bool DependenciesReady() => false;

    protected override bool AnyDependencyFailed() => Interlocked.Increment(ref _failedLooks) > 1;
  }

  /// <summary>
  /// Verifies that starting a task whose dependency fails while the Start transition is decided does not throw, and
  /// ends in a state that follows from one look at the dependencies.
  /// </summary>
  [Fact]
  public async Task EngineTask_Start_DependencyFailsWhileDeciding_DoesNotThrow()
  {
    var dependency = new EngineTask("Dependency", _ => Task.CompletedTask, true, NullLogger.Instance, CancellationToken.None);
    var task = new RacingTask();
    task.AddDependsOn(dependency);

    var start = await Record.ExceptionAsync(() => task.FireAsync(EngineTaskTrigger.Start));

    start.Should().BeNull();
    task.State.Should().BeOneOf(EngineTaskState.WaitingForDependencies, EngineTaskState.DependencyFailed);
  }

  /// <summary>
  /// A task whose dependencies are not ready at the first look (when it starts), ready at the second (a dependency
  /// completed), and not ready again from the third on (that dependency is reset at the same time).
  /// </summary>
  private sealed class ResettingDependencyTask() : EngineTask("Dependent", _ => Task.CompletedTask, true, NullLogger.Instance, CancellationToken.None)
  {
    private int _readyLooks;

    protected override bool DependenciesReady() => Interlocked.Increment(ref _readyLooks) == 2;

    protected override bool AnyDependencyFailed() => false;
  }

  /// <summary>
  /// Verifies that a completed dependency takes a waiting task to one state, also when that dependency is reset while
  /// the task decides.
  /// </summary>
  [Fact]
  public async Task EngineTask_CompleteDependency_DependencyResetWhileDeciding_TakesOneDecision()
  {
    var dependency = new EngineTask("Dependency", _ => Task.CompletedTask, true, NullLogger.Instance, CancellationToken.None);
    var task = new ResettingDependencyTask();
    task.AddDependsOn(dependency);
    await task.FireAsync(EngineTaskTrigger.Start);
    task.State.Should().Be(EngineTaskState.WaitingForDependencies);

    var complete = await Record.ExceptionAsync(() => task.FireAsync(EngineTaskTrigger.CompleteDependency));

    complete.Should().BeNull();
    task.State.Should().BeOneOf(EngineTaskState.Running, EngineTaskState.CompletedWithSuccess);
  }

  /// <summary>
  /// Verifies that a dependency callback that cannot fire its trigger does not throw out of the dependency's
  /// transition (an async void handler would end the process), and the dependency still completes.
  /// </summary>
  [Fact]
  public async Task EngineTask_DependencyCallback_FiringThrows_IsLoggedNotThrown()
  {
    var ct = TestContext.Current.CancellationToken;
    var dependency = new EngineTask("Dependency", _ => Task.CompletedTask, true, NullLogger.Instance, CancellationToken.None);
    var task = new ThrowingTask();
    task.AddDependsOn(dependency);
    await task.FireAsync(EngineTaskTrigger.Start);

    await dependency.FireAsync(EngineTaskTrigger.Start);
    await dependency.InnerTask.WaitAsync(TimeSpan.FromSeconds(5), ct);

    SpinWait.SpinUntil(() => task.Looks > 1, TimeSpan.FromSeconds(5)).Should().BeTrue("the completion callback runs");
    dependency.State.Should().Be(EngineTaskState.CompletedWithSuccess);
  }

  /// <summary>
  /// A task whose readiness check throws once it has started waiting, standing in for any unexpected failure while a
  /// dependency callback fires its trigger.
  /// </summary>
  private sealed class ThrowingTask() : EngineTask("Dependent", _ => Task.CompletedTask, true, NullLogger.Instance, CancellationToken.None)
  {
    private int _looks;

    public int Looks => Volatile.Read(ref _looks);

    protected override bool DependenciesReady() =>
      Interlocked.Increment(ref _looks) > 1 ? throw new InvalidOperationException("Unexpected.") : false;
  }
}
