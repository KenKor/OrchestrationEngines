using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Restarter;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for how the coordinator computes its state: from its own start and shutdown, and from the states of
/// the engines registered as required for running.
/// </summary>
public class CoordinatorComputedStateTest
{
  /// <summary>
  /// An engine without tasks: Ready once started, after which the test sets its state. Its stop waits for
  /// <see cref="HoldStop"/>. Not <see cref="StopFailingEngine"/>: <see cref="Stopping"/> is signalled before the base
  /// stop, so a test sees the engine still Ready while the coordinator is Stopping.
  /// </summary>
  private sealed class PlainEngine() : BaseEngine(NullLogger.Instance)
  {
    public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task HoldStop { get; set; } = Task.CompletedTask;

    protected override void SetUpTasks()
    {
    }

    public override async Task StopAsync()
    {
      Stopping.TrySetResult();
      await HoldStop;
      await base.StopAsync();
    }
  }

  private static EngineCoordinator CreateCoordinator(params (string Name, IEngine Engine, bool Required)[] engines)
  {
    var coordinator = new EngineCoordinator(nameof(CoordinatorComputedStateTest), NullLogger.Instance);
    foreach (var (name, engine, required) in engines)
      coordinator.RegisterEngine(name, _ => Task.FromResult(engine), requiredForRunning: required)
        .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));
    return coordinator;
  }

  /// <summary>
  /// Verifies the state of a started coordinator for the states of two required engines and an optional one: a
  /// Faulted or Stopped required engine comes before a Starting one, and the optional engine does not count.
  /// </summary>
  [Theory]
  [InlineData(EngineState.Ready, EngineState.Ready, EngineState.Ready, EngineCoordinatorState.Running)]
  [InlineData(EngineState.Ready, EngineState.Ready, EngineState.Faulted, EngineCoordinatorState.Running)]
  [InlineData(EngineState.Ready, EngineState.Ready, EngineState.Starting, EngineCoordinatorState.Running)]
  [InlineData(EngineState.Starting, EngineState.Ready, EngineState.Ready, EngineCoordinatorState.Initializing)]
  [InlineData(EngineState.Starting, EngineState.Starting, EngineState.Ready, EngineCoordinatorState.Initializing)]
  [InlineData(EngineState.Faulted, EngineState.Ready, EngineState.Ready, EngineCoordinatorState.WaitingForEngines)]
  [InlineData(EngineState.Stopped, EngineState.Ready, EngineState.Ready, EngineCoordinatorState.WaitingForEngines)]
  [InlineData(EngineState.Faulted, EngineState.Starting, EngineState.Ready, EngineCoordinatorState.WaitingForEngines)]
  [InlineData(EngineState.Starting, EngineState.Stopped, EngineState.Ready, EngineCoordinatorState.WaitingForEngines)]
  public async Task EngineCoordinator_State_Started_FollowsTheRequiredEngines(EngineState first, EngineState second,
    EngineState optional, EngineCoordinatorState expected)
  {
    var ct = TestContext.Current.CancellationToken;
    var (a, b, c) = (new PlainEngine(), new PlainEngine(), new PlainEngine());
    var coordinator = CreateCoordinator(("A", a, true), ("B", b, true), ("C", c, false));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    (await coordinator.GetEngineAsync("C", ct).WaitAsync(MustComplete, ct)).Should().BeSameAs(c);

    a.State = first;
    b.State = second;
    c.State = optional;

    coordinator.State.Should().Be(expected);
  }

  /// <summary>
  /// Verifies that the state follows the coordinator's own start and shutdown: Created before the start, Stopping
  /// while the shutdown stops the engines, and Stopped after it, whatever the engines say.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_State_BeforeStartAndDuringAndAfterShutdown_FollowsTheCoordinator()
  {
    var ct = TestContext.Current.CancellationToken;
    var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = new PlainEngine { HoldStop = releaseStop.Task };
    var coordinator = CreateCoordinator(("A", engine, true));

    coordinator.State.Should().Be(EngineCoordinatorState.Created);

    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    coordinator.State.Should().Be(EngineCoordinatorState.Running);

    var shutdown = coordinator.ShutdownAsync(ct);
    await engine.Stopping.Task.WaitAsync(MustComplete, ct);
    engine.State.Should().Be(EngineState.Ready);
    coordinator.State.Should().Be(EngineCoordinatorState.Stopping);

    releaseStop.SetResult();
    await shutdown.WaitAsync(MustComplete, ct);
    engine.State = EngineState.Ready;
    coordinator.State.Should().Be(EngineCoordinatorState.Stopped);
  }

  /// <summary>
  /// Verifies that a required engine that failed, while its kick-start waits to retry it, counts as starting: the
  /// coordinator is Initializing, as a lookup waits for the retry.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_State_RequiredEngineFailedAndRetryPending_IsInitializing()
  {
    var ct = TestContext.Current.CancellationToken;
    var engine = new FailingEngine();
    var coordinator = new EngineCoordinator(nameof(CoordinatorComputedStateTest), NullLogger.Instance);
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .SetRestartPolicy(new RestartPolicy
      {
        MaxRestartsInWindow = 5,
        Window = TimeSpan.FromMinutes(10),
        InitialBackoff = TimeSpan.FromMinutes(5),
        MaxBackoff = TimeSpan.FromMinutes(5),
      });
    _ = coordinator.StartAsync();
    await Wait.UntilAsync(() => engine.State == EngineState.Faulted, MustComplete, "the first load must fail");

    coordinator.State.Should().Be(EngineCoordinatorState.Initializing);
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that a required engine whose creation failed for good, so that it never exists, counts as Faulted: the
  /// coordinator is WaitingForEngines.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_State_RequiredEngineNotCreated_IsWaitingForEngines()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator(nameof(CoordinatorComputedStateTest), NullLogger.Instance);
    coordinator.RegisterEngine("A", _ => throw new InvalidOperationException("The engine could not be created."),
        requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();
    await coordinator.StartAsync();

    (await coordinator.GetEngineAsync("A", throwErrorIfNotFound: false, ct).WaitAsync(MustComplete, ct))
      .Should().BeNull();
    coordinator.State.Should().Be(EngineCoordinatorState.WaitingForEngines);
  }

  /// <summary>
  /// Verifies that a coordinator without required engines whose start failed is not Running: no engine counts, but
  /// the failed start does.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_State_NoRequiredEngineAndStartFailed_IsWaitingForEngines()
  {
    var coordinator = new EngineCoordinator(nameof(CoordinatorComputedStateTest), NullLogger.Instance);
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(new PlainEngine()), requiredForRunning: false)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .AddDependsOn("Missing");

    var start = await Record.ExceptionAsync(() => coordinator.StartAsync());

    start.Should().NotBeNull();
    coordinator.State.Should().Be(EngineCoordinatorState.WaitingForEngines);
  }

  /// <summary>
  /// An engine whose load fails.
  /// </summary>
  private sealed class FailingEngine() : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() =>
      CreateTask("Load", _ => throw new InvalidOperationException("The database was not reachable."));
  }
}
