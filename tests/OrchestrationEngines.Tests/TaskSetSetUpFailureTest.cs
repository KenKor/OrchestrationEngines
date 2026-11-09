using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Restarter;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for a start whose task set cannot be built: a cycle in its dependencies, or a set-up that throws.
/// None of that set may be published, the engine must be Faulted, and whoever waits on it must not wait forever.
/// </summary>
public class TaskSetSetUpFailureTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// An engine whose set-up the test chooses per start.
  /// </summary>
  private sealed class SetUpEngine : BaseEngine
  {
    public SetUpEngine() : base(NullLogger.Instance) => Context = new NoContext();

    public Action<SetUpEngine> SetUp { get; set; }

    public int SetUps;

    public EngineTask Make(string name) => CreateTask(name, _ => Task.CompletedTask);

    public EngineTask Make(string name, Func<CancellationToken, Task> work) => CreateTask(name, work);

    public void WaitFor(Task work) => StartAfter(work);

    protected override void SetUpTasks()
    {
      Interlocked.Increment(ref SetUps);
      SetUp(this);
    }
  }

  private static void Cyclic(SetUpEngine e)
  {
    var a = e.Make("A");
    var b = e.Make("B").AddDependsOn(a);
    a.AddDependsOn(b);
  }

  private static void Valid(SetUpEngine e) => e.Make("B").AddDependsOn(e.Make("A"));

  /// <summary>
  /// Verifies that a cyclic set is not published and leaves the engine Faulted, and that a wait for the engine ends.
  /// Without the fix, the cyclic set was published before the check: the engine stayed Created and the wait hung on
  /// tasks that wait for each other.
  /// </summary>
  [Fact]
  public async Task StartAsync_CyclicTaskSet_FaultsWithoutPublishingTheSet()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new SetUpEngine { SetUp = Cyclic };

    var start = await Record.ExceptionAsync(() => engine.StartAsync());

    start.Should().BeOfType<CyclicEngineTaskDependencyException>();
    engine.State.Should().Be(EngineState.Faulted);
    engine.Tasks.Should().BeEmpty("no part of a set that failed its check may be published");
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    engine.State.Should().Be(EngineState.Faulted);
  }

  /// <summary>
  /// Verifies that a set-up that throws faults the engine the same way, and does not publish the tasks it created
  /// before it threw.
  /// </summary>
  [Fact]
  public async Task StartAsync_SetUpThrows_FaultsWithoutPublishingTheSet()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new SetUpEngine
    {
      SetUp = e =>
      {
        e.Make("A");
        throw new InvalidOperationException("The set-up fails.");
      }
    };

    var start = await Record.ExceptionAsync(() => engine.StartAsync());

    start.Should().BeOfType<InvalidOperationException>();
    engine.State.Should().Be(EngineState.Faulted);
    engine.Tasks.Should().BeEmpty();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that a failed set-up replaces a running set too, and that the next start with a valid set-up makes the
  /// engine Ready again: the failure does not stick.
  /// </summary>
  [Fact]
  public async Task StartAsync_AfterAFailedSetUp_ValidSetUpMakesTheEngineReady()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new SetUpEngine { SetUp = Valid };
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    engine.State.Should().Be(EngineState.Ready);

    engine.SetUp = Cyclic;
    var restart = await Record.ExceptionAsync(() => engine.StartAsync());
    restart.Should().BeOfType<CyclicEngineTaskDependencyException>();
    engine.State.Should().Be(EngineState.Faulted);
    engine.Tasks.Should().BeEmpty("the previous set was replaced");

    engine.SetUp = Valid;
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    engine.State.Should().Be(EngineState.Ready);
    engine.Tasks.Should().HaveCount(2);
  }

  /// <summary>
  /// Verifies that the coordinator's recovery starts an engine again whose set-up failed once, as it did before a
  /// failed set-up left the engine Faulted. Without the fix in PrepareRestartAsync, the engine had no tasks to reset,
  /// stayed Faulted, was never started again, and the recovery gave up.
  /// </summary>
  [Fact]
  public async Task Coordinator_SetUpFailsOnce_RecoveryMakesTheEngineReady()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new SetUpEngine();
    engine.SetUp = e =>
    {
      if (Volatile.Read(ref e.SetUps) == 1)
        throw new InvalidOperationException("The first set-up fails.");
      Valid(e);
    };
    var coordinator = new EngineCoordinator(nameof(TaskSetSetUpFailureTest), NullLogger.Instance, useDispatcher: false);
    coordinator.RegisterEngine("Engine", _ => Task.FromResult<IEngine>(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .SetRecoveryHelper(new RestartPolicy
      {
        MaxRestartsInWindow = 3,
        InitialBackoff = TimeSpan.Zero,
        MaxBackoff = TimeSpan.Zero,
      });

    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var deadline = DateTime.UtcNow + MustComplete;
    while (engine.State != EngineState.Ready && DateTime.UtcNow < deadline)
      await Task.Delay(10, ct);

    engine.State.Should().Be(EngineState.Ready);
    Volatile.Read(ref engine.SetUps).Should().Be(2);
  }

  /// <summary>
  /// Verifies that the work a failed set-up's set had to wait for is waited for by the next set. Without the fix, the
  /// failed set-up dropped it, and the next set ran next to that work.
  /// </summary>
  [Fact]
  public async Task StartAsync_AfterAFailedSetUp_TheNextSetStillWaitsForTheInheritedWork()
  {
    var ct = TestContext.Current.CancellationToken;
    var inherited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var ran = 0;
    var engine = new SetUpEngine { SetUp = Cyclic };
    engine.WaitFor(inherited.Task);
    (await Record.ExceptionAsync(() => engine.StartAsync())).Should().BeOfType<CyclicEngineTaskDependencyException>();

    engine.SetUp = e => e.Make("A", _ =>
    {
      Interlocked.Increment(ref ran);
      return Task.CompletedTask;
    });
    await engine.StartAsync();
    await Task.Delay(200, ct);
    Volatile.Read(ref ran).Should().Be(0, "the inherited work has not ended yet");

    inherited.SetResult();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    Volatile.Read(ref ran).Should().Be(1);
  }

  /// <summary>
  /// Verifies that a start after a failed set-up announces Starting before Ready, as any other start does.
  /// </summary>
  [Fact]
  public async Task StartAsync_AfterAFailedSetUp_PublishesStarting()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new SetUpEngine { SetUp = Cyclic };
    (await Record.ExceptionAsync(() => engine.StartAsync())).Should().BeOfType<CyclicEngineTaskDependencyException>();
    var states = new List<EngineState>();
    engine.EngineStatusChanged += (_, e) =>
    {
      lock (states)
        states.Add(e.State);
    };

    engine.SetUp = Valid;
    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    lock (states)
      states.Should().Equal(EngineState.Starting, EngineState.Ready);
  }
}
