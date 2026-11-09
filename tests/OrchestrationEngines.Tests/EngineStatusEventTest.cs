using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the engine status events, which a host shows in its UI or health endpoint: every engine
/// announces Starting and Ready in that order, again on every reload, Faulted when it fails, and Stopped on
/// shutdown, and an engine that is not touched announces nothing.
/// </summary>
public class EngineStatusEventTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// An engine with one load task; the test decides what a load does.
  /// </summary>
  private sealed class LoadEngine(Func<CancellationToken, Task> loadAsync = null) : BaseEngine(NullLogger.Instance)
  {
    /// <summary>
    /// The changes the hook was told about, as (old, new).
    /// </summary>
    public ConcurrentQueue<(EngineState oldValue, EngineState newValue)> Changes { get; } = new();

    protected override void SetUpTasks() => CreateTask("Load", loadAsync ?? (_ => Task.CompletedTask));

    protected override void OnEngineStatusChanged(EngineState oldValue, EngineState value)
    {
      Changes.Enqueue((oldValue, value));
      base.OnEngineStatusChanged(oldValue, value);
    }
  }

  /// <summary>
  /// Records the status events of the engines it is attached to, in the order they arrived. The event lives on
  /// BaseEngine, not on IEngine, so a host binds to the concrete engines.
  /// </summary>
  private sealed class StatusLog
  {
    private readonly ConcurrentQueue<(string engine, EngineState state)> _events = new();

    public void Attach(string name, BaseEngine engine) =>
      engine.EngineStatusChanged += (_, e) => _events.Enqueue((name, e.State));

    public IReadOnlyList<EngineState> For(string name) =>
      _events.Where(e => e.engine == name).Select(e => e.state).ToList();

    public void Clear() => _events.Clear();
  }

  private static EngineCoordinator CreateCoordinator() =>
    new(nameof(EngineStatusEventTest), NullLogger.Instance, useDispatcher: false);

  private static void Register(EngineCoordinator coordinator, string name, BaseEngine engine, StatusLog log, string dependsOn = null)
  {
    log.Attach(name, engine);
    var descriptor = coordinator.RegisterEngine(name, _ => Task.FromResult<IEngine>(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      // A failed load must fault at once, not retry by the default restart policy.
      .WithoutRestarts();
    if (dependsOn != null)
      descriptor.AddDependsOn(dependsOn);
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
  /// Verifies that an engine announces Starting and then Ready, once each, while it starts.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EngineStatusChanged_OnStart_ReportsStartingThenReady()
  {
    var ct = TestContext.Current.CancellationToken;
    var log = new StatusLog();
    var coordinator = CreateCoordinator();
    Register(coordinator, "E", new LoadEngine(), log);

    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    log.For("E").Should().Equal(EngineState.Starting, EngineState.Ready);
  }

  /// <summary>
  /// Verifies that invalidating an engine's context makes it and its dependent announce the reload (Created for the
  /// reset, then Starting and Ready), while an independent engine announces nothing.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EngineStatusChanged_OnInvalidate_DependentsReportAgainOthersStaySilent()
  {
    var ct = TestContext.Current.CancellationToken;
    var log = new StatusLog();
    var coordinator = CreateCoordinator();
    Register(coordinator, "Session", new LoadEngine(), log);
    Register(coordinator, "Kernel", new LoadEngine(), log, dependsOn: "Session");
    Register(coordinator, "Map", new LoadEngine(), log);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    log.Clear();

    await coordinator.InvalidateContextForEngineAsync("Session", ct).WaitAsync(MustComplete, ct);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    log.For("Session").Should().Equal(EngineState.Created, EngineState.Starting, EngineState.Ready);
    log.For("Kernel").Should().Equal(EngineState.Created, EngineState.Starting, EngineState.Ready);
    log.For("Map").Should().BeEmpty();
  }

  /// <summary>
  /// Verifies that an engine reset by its host (its process died) announces Starting and Ready again, also when its
  /// load is over before anybody looks: the announcement is made before the tasks run, not derived afterwards.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EngineStatusChanged_OnTriggerReset_ReportsStartingThenReadyAgain()
  {
    var ct = TestContext.Current.CancellationToken;
    var log = new StatusLog();
    var coordinator = CreateCoordinator();
    var engine = new LoadEngine();
    Register(coordinator, "E", engine, log);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    log.Clear();

    await engine.TriggerResetAsync().WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => log.For("E").LastOrDefault() == EngineState.Ready, "the reset engine loads again");

    log.For("E").Should().Equal(EngineState.Starting, EngineState.Ready);
  }

  /// <summary>
  /// Verifies that an engine whose load fails announces Faulted, and names the failed task with its reason on the
  /// task itself, where a host finds what to show.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EngineStatusChanged_OnFailure_ReportsFaultedAndTheTaskKeepsTheReason()
  {
    var ct = TestContext.Current.CancellationToken;
    var log = new StatusLog();
    var coordinator = CreateCoordinator();
    var engine = new LoadEngine(_ => throw new InvalidOperationException("No licence."));
    Register(coordinator, "E", engine, log);

    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => engine.State == EngineState.Faulted, "the load throws");

    log.For("E").Last().Should().Be(EngineState.Faulted);
    var failed = engine.Tasks.Values.Single(t => t.IsFailed);
    failed.Name.Should().Be("Load");
    failed.FailReason.Should().Contain("No licence.");
  }

  /// <summary>
  /// Verifies that state changes are delivered in the order they were made, one at a time: a subscriber that changes
  /// the state from its handler (as the coordinator does, through its state machine) gets that change after the
  /// current one has been delivered, not nested inside it. The same mechanism keeps changes made on different threads
  /// in order, which a nested delivery or a preempted publisher would not.
  /// </summary>
  [Fact]
  public void BaseEngine_EngineStatusChanged_AChangeMadeFromAHandlerIsDeliveredAfterTheCurrentOne()
  {
    var engine = new LoadEngine();
    var delivered = new List<(EngineState state, int depth)>();
    var depth = 0;
    engine.EngineStatusChanged += (_, e) =>
    {
      depth++;
      delivered.Add((e.State, depth));
      if (e.State == EngineState.Starting)
        engine.State = EngineState.Ready;
      depth--;
    };

    engine.State = EngineState.Starting;

    delivered.Should().Equal((EngineState.Starting, 1), (EngineState.Ready, 1));
    engine.State.Should().Be(EngineState.Ready);
  }

  /// <summary>
  /// Verifies that setting the state raises EngineStatusChanged once with the new value, and calls the hook once with
  /// the old and the new value.
  /// </summary>
  [Fact]
  public void BaseEngine_State_Set_RaisesTheEventOnceWithTheOldAndNewValue()
  {
    var engine = new LoadEngine();
    var events = new List<EngineState>();
    engine.EngineStatusChanged += (_, e) => events.Add(e.State);

    engine.State = EngineState.Starting;

    engine.State.Should().Be(EngineState.Starting);
    events.Should().Equal(EngineState.Starting);
    engine.Changes.Should().Equal((EngineState.Created, EngineState.Starting));
  }

  /// <summary>
  /// Verifies that setting the state it already has publishes nothing.
  /// </summary>
  [Fact]
  public void BaseEngine_State_SetToTheSameValue_RaisesNothing()
  {
    var engine = new LoadEngine();
    var events = new List<EngineState>();
    engine.EngineStatusChanged += (_, e) => events.Add(e.State);

    engine.State = EngineState.Created;

    events.Should().BeEmpty();
    engine.Changes.Should().BeEmpty();
  }

  /// <summary>
  /// Verifies that shutting the coordinator down ends every engine in Stopped, announced as such, and the
  /// coordinator in Stopped.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_ShutdownAsync_EveryEngineReportsStopped()
  {
    var ct = TestContext.Current.CancellationToken;
    var log = new StatusLog();
    var coordinator = CreateCoordinator();
    var session = new LoadEngine();
    var kernel = new LoadEngine();
    Register(coordinator, "Session", session, log);
    Register(coordinator, "Kernel", kernel, log, dependsOn: "Session");
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    log.Clear();

    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => session.State == EngineState.Stopped && kernel.State == EngineState.Stopped, "shutdown stops the engines");

    coordinator.State.Should().Be(EngineCoordinatorState.Stopped);
    log.For("Session").Should().Equal(EngineState.Stopped);
    log.For("Kernel").Should().Equal(EngineState.Stopped);
  }
}
