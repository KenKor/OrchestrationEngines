using System;
using System.Collections.Generic;
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
    public SetUpEngine(ILogger logger = null) : base(logger ?? NullLogger.Instance) => Context = new NoContext();

    public Action<SetUpEngine> SetUp { get; set; }

    public int SetUps;

    public EngineTask Make(string name) => Make(name, _ => Task.CompletedTask);

    public EngineTask Make(string name, Func<CancellationToken, Task> work) => CreateTask(name, work);

    public void WaitFor(Task work) => StartAfter(work);

    protected override void SetUpTasks()
    {
      Interlocked.Increment(ref SetUps);
      SetUp(this);
    }
  }

  /// <summary>
  /// A logger that runs an action when the engine logs that its set-up failed.
  /// </summary>
  private sealed class OnSetUpFailedLogger(Action onSetUpFailed) : ILogger
  {
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
      Func<TState, Exception, string> formatter)
    {
      if (formatter(state, exception).Contains(SetUpFailedMessage))
        onSetUpFailed();
    }
  }

  private const string SetUpFailedMessage = "Setting up tasks and dependencies failed";

  private static async Task WaitUntilAsync(Func<bool> condition, string because)
  {
    var deadline = DateTime.UtcNow + MustComplete;
    while (!condition())
    {
      DateTime.UtcNow.Should().BeBefore(deadline, because);
      await Task.Delay(10, TestContext.Current.CancellationToken);
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
  /// Verifies that the set's check finds a cycle of any length, a task that depends on itself included, and names a
  /// task on the cycle, also when the cycle is reached only through a task in front of it.
  /// </summary>
  [Theory]
  [InlineData(1)]
  [InlineData(2)]
  [InlineData(3)]
  public async Task StartAsync_CycleOfAnyLength_IsFoundAndNamesATaskOnIt(int length)
  {
    var engine = new SetUpEngine
    {
      SetUp = e =>
      {
        var cycle = Enumerable.Range(0, length).Select(i => e.Make($"C{i}")).ToArray();
        for (var i = 0; i < length; i++)
          cycle[i].AddDependsOn(cycle[(i + 1) % length]);
        e.Make("Front").AddDependsOn(cycle[0]);
      }
    };

    var start = await Record.ExceptionAsync(() => engine.StartAsync());

    start.Should().BeOfType<CyclicEngineTaskDependencyException>()
      .Which.Message.Should().MatchRegex("'C[0-9]+'", "the task named is on the cycle, not the one in front of it");
    engine.State.Should().Be(EngineState.Faulted);
  }

  /// <summary>
  /// Verifies that tasks that share dependencies without a cycle (a diamond, and a long chain under it) pass the check.
  /// </summary>
  [Fact]
  public async Task StartAsync_SharedDependenciesWithoutACycle_PassTheCheck()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new SetUpEngine
    {
      SetUp = e =>
      {
        var bottom = e.Make("Bottom");
        var below = bottom;
        for (var i = 0; i < 200; i++)
          below = e.Make($"Chain{i}").AddDependsOn(below);
        var left = e.Make("Left").AddDependsOn(below);
        var right = e.Make("Right").AddDependsOn(below);
        e.Make("Top").AddDependsOn(left).AddDependsOn(right);
      }
    };

    await engine.StartAsync();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);

    engine.State.Should().Be(EngineState.Ready);
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
    await WaitUntilAsync(() => engine.State == EngineState.Ready, "recovery must start the engine again");
    await WaitUntilAsync(() => coordinator.State == EngineCoordinatorState.Running, "the coordinator must run again");
    Volatile.Read(ref engine.SetUps).Should().Be(2);
  }

  /// <summary>
  /// Verifies that the work a failed set-up's set had to wait for is waited for by the next set, also when StartAfter
  /// gives more work before the retry: that work is added to the handed-on work, not put in its place. Without the
  /// fixes, the failed set-up dropped the work, or StartAfter overwrote it, and the next set ran next to it.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task StartAsync_AfterAFailedSetUp_TheNextSetStillWaitsForTheHandedOnWork(bool startAfterOnTheRetry)
  {
    var ct = TestContext.Current.CancellationToken;
    var handedOn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var ran = 0;
    var engine = new SetUpEngine { SetUp = Cyclic };
    engine.WaitFor(handedOn.Task);
    (await Record.ExceptionAsync(() => engine.StartAsync())).Should().BeOfType<CyclicEngineTaskDependencyException>();

    engine.SetUp = e => e.Make("A", _ =>
    {
      Interlocked.Increment(ref ran);
      return Task.CompletedTask;
    });
    if (startAfterOnTheRetry)
      engine.WaitFor(Task.CompletedTask);
    await engine.StartAsync();
    await Task.Delay(200, ct);
    Volatile.Read(ref ran).Should().Be(0, "the handed-on work has not ended yet");

    handedOn.SetResult();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    Volatile.Read(ref ran).Should().Be(1);
  }

  /// <summary>
  /// Verifies that StartAfter ignores null, also once a failed set-up has handed on work, as it did before work could
  /// be handed on. Without the fix, it threw there, since the null was combined with the handed-on work.
  /// </summary>
  [Fact]
  public async Task StartAfter_Null_AfterAFailedSetUp_IsIgnored()
  {
    var ct = TestContext.Current.CancellationToken;
    var handedOn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = new SetUpEngine { SetUp = Cyclic };
    engine.WaitFor(handedOn.Task);
    (await Record.ExceptionAsync(() => engine.StartAsync())).Should().BeOfType<CyclicEngineTaskDependencyException>();

    engine.Invoking(e => e.WaitFor(null)).Should().NotThrow();

    engine.SetUp = Valid;
    await engine.StartAsync();
    handedOn.SetResult();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    engine.State.Should().Be(EngineState.Ready);
  }

  /// <summary>
  /// Verifies that the same work given more than once, and work that has already ended, are waited for once, and that
  /// the next set still waits for all work that is still running.
  /// </summary>
  [Fact]
  public async Task StartAfter_SameWorkRepeatedOverRetries_TheNextSetWaitsForAllOfIt()
  {
    var ct = TestContext.Current.CancellationToken;
    var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var ran = 0;
    var engine = new SetUpEngine { SetUp = Cyclic };
    for (var retry = 0; retry < 3; retry++)
    {
      engine.WaitFor(first.Task);
      engine.WaitFor(Task.CompletedTask);
      (await Record.ExceptionAsync(() => engine.StartAsync())).Should().BeOfType<CyclicEngineTaskDependencyException>();
    }
    engine.WaitFor(second.Task);

    engine.SetUp = e => e.Make("A", _ =>
    {
      Interlocked.Increment(ref ran);
      return Task.CompletedTask;
    });
    await engine.StartAsync();
    first.SetResult();
    await Task.Delay(200, ct);
    Volatile.Read(ref ran).Should().Be(0, "the second piece of work has not ended yet");

    second.SetResult();
    await engine.EnsureReadyAsync().WaitAsync(MustComplete, ct);
    Volatile.Read(ref ran).Should().Be(1);
  }

  /// <summary>
  /// Verifies that a set-up that throws because the engine was stopped meanwhile leaves the engine Stopped, not
  /// Faulted: the stop is not a failure.
  /// </summary>
  [Fact]
  public async Task StartAsync_StoppedDuringSetUp_IsStopped()
  {
    var engine = new SetUpEngine
    {
      SetUp = e =>
      {
        e.StopAsync().GetAwaiter().GetResult();
        throw new OperationCanceledException();
      }
    };

    (await Record.ExceptionAsync(() => engine.StartAsync())).Should().BeOfType<OperationCanceledException>();

    engine.State.Should().Be(EngineState.Stopped);
  }

  /// <summary>
  /// Verifies that a stop that comes in after the set-up failed, but before the engine's state was decided, leaves the
  /// engine Stopped: the stop is final. The failure log is the only call in that window, so the stop comes from there.
  /// Without the fix, the engine was found not stopped, the stop then published Stopped, and Faulted overwrote it.
  /// </summary>
  [Fact]
  public async Task StartAsync_StoppedWhileTheFailureIsLogged_IsStopped()
  {
    SetUpEngine engine = null;
    engine = new SetUpEngine(new OnSetUpFailedLogger(() => engine.StopAsync().GetAwaiter().GetResult()))
    {
      SetUp = _ => throw new InvalidOperationException("The set-up fails."),
    };
    var states = new List<EngineState>();
    engine.EngineStatusChanged += (_, e) =>
    {
      lock (states)
        states.Add(e.State);
    };

    (await Record.ExceptionAsync(() => engine.StartAsync())).Should().BeOfType<InvalidOperationException>();

    engine.State.Should().Be(EngineState.Stopped);
    lock (states)
      states.Should().NotContain(EngineState.Faulted);
  }

  /// <summary>
  /// Verifies which set-up failures are logged and what state they leave: only a cancellation during a stop is part of
  /// the stop, so it is not logged and the engine is Stopped. Any other failure is logged, also during a stop (which
  /// still leaves the engine Stopped); and a cancellation without a stop is a failure like any other. Without the fix, a
  /// real failure during a stop was not logged.
  /// </summary>
  [Theory]
  [InlineData(true, true, false, EngineState.Stopped)]
  [InlineData(true, false, true, EngineState.Stopped)]
  [InlineData(false, true, true, EngineState.Faulted)]
  [InlineData(false, false, true, EngineState.Faulted)]
  public async Task StartAsync_SetUpFails_LogsUnlessItIsTheStopsCancellation(
    bool stop, bool cancellation, bool logged, EngineState expected)
  {
    var logger = new FakeLogger();
    var engine = new SetUpEngine(logger)
    {
      SetUp = e =>
      {
        if (stop)
          e.StopAsync().GetAwaiter().GetResult();
        throw cancellation ? new OperationCanceledException() : new InvalidOperationException("The set-up fails.");
      }
    };

    await Record.ExceptionAsync(() => engine.StartAsync());

    engine.State.Should().Be(expected);
    logger.Collector.GetSnapshot()
      .Any(r => r.Level == LogLevel.Error && r.Message.Contains(SetUpFailedMessage))
      .Should().Be(logged);
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
