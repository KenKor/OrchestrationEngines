using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Mechanic;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for one engine feeding several dependents, the shape of a host whose session (an open workspace,
/// a connection) is an engine the others build on: replacing the session's context must reload every dependent
/// with the new context, leave the independent engines alone, and keep the coordinator Running throughout.
/// </summary>
public class ContextFanOutTest
{
  /// <summary>
  /// A context that carries a value, for example the path of the open workspace.
  /// </summary>
  private sealed class ValueContext(string value) : BaseEngineContext
  {
    public string Value { get; } = value;

    public override bool Equals(IEngineContext other) => other is ValueContext o && o.Value == Value;

    public override bool Equals(object obj) => obj is IEngineContext ctx && Equals(ctx);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);
  }

  /// <summary>
  /// An engine whose load records the value of the context it loaded with: its own, or when it depends on the
  /// session engine, the session's. The session engine is reached through the context, as the Engine wires it.
  /// </summary>
  private sealed class RecordingEngine(string sessionName = null) : BaseEngine(NullLogger.Instance)
  {
    private readonly ConcurrentQueue<string> _loadedWith = new();

    public IReadOnlyCollection<string> LoadedWith => _loadedWith;

    protected override void SetUpTasks() => CreateTask("Load", _ =>
    {
      var source = sessionName == null ? this : (RecordingEngine)Context.GetUnderlyingEngine(sessionName);
      _loadedWith.Enqueue((source.Context.Inner ?? source.Context) is ValueContext v ? v.Value : "<none>");
      return Task.CompletedTask;
    });
  }

  /// <summary>
  /// An engine that records, on each load, the value of the context it was set up with, and whose start, once
  /// armed, ends only when the test lets it. Held after the set-up, its load has run by then, but the kick-start that
  /// started it is still waiting for the start. Held before the set-up, the engine is still Created while the
  /// kick-start starts it.
  /// </summary>
  private sealed class HeldStartEngine : BaseEngine
  {
    private readonly ConcurrentQueue<string> _loadedWith = new();
    private readonly bool _withMonitor;
    private TaskCompletionSource _hold;
    private bool _holdBeforeSetUp;
    private TaskCompletionSource _endHold;

    /// <param name="withMonitor">Adds a task that keeps running, as a monitor does, and whose end can be held (see
    /// <see cref="HoldNextEnd"/>).</param>
    /// <param name="logger">The engine's logger.</param>
    public HeldStartEngine(bool withMonitor = false, ILogger logger = null) : base(logger ?? NullLogger.Instance) =>
      _withMonitor = withMonitor;

    public IReadOnlyCollection<string> LoadedWith => _loadedWith;

    /// <summary>Completed once a held start has begun waiting for the test.</summary>
    public TaskCompletionSource StartHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource HoldNextStart(bool beforeSetUp = false)
    {
      _holdBeforeSetUp = beforeSetUp;
      return _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public override async Task StartAsync()
    {
      var hold = Interlocked.Exchange(ref _hold, null);
      if (hold != null && _holdBeforeSetUp)
        await WaitAsync(hold).ConfigureAwait(false);
      await base.StartAsync().ConfigureAwait(false);
      if (hold != null && !_holdBeforeSetUp)
        await WaitAsync(hold).ConfigureAwait(false);
    }

    private Task WaitAsync(TaskCompletionSource hold)
    {
      StartHeld.TrySetResult();
      return hold.Task;
    }

    /// <summary>
    /// Holds the next end of the monitor task, inside its cancellation, until the test completes the returned source: a
    /// restart of the engine that ends its tasks waits until then.
    /// </summary>
    public TaskCompletionSource HoldNextEnd() =>
      _endHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed once a held end has begun waiting for the test.</summary>
    public TaskCompletionSource EndHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override void SetUpTasks()
    {
      var loadWith = (Context.Inner ?? Context) is ValueContext v ? v.Value : "<none>";
      CreateTask("Load", _ =>
      {
        _loadedWith.Enqueue(loadWith);
        return Task.CompletedTask;
      });
      if (_withMonitor)
        AddTask("Monitor", new MonitorTask(this));
    }

    /// <summary>
    /// A task that runs until it is cancelled. Its cancellation waits for the test once the engine holds its next end.
    /// </summary>
    private sealed class MonitorTask(HeldStartEngine engine) : EngineTask("Monitor",
      ct => Task.Delay(System.Threading.Timeout.Infinite, ct), requiredForRunning: false, NullLogger.Instance,
      engine.CancellationToken)
    {
      protected override void OnFailedEntered(string reasonFromTrigger = null, bool cancelled = false)
      {
        base.OnFailedEntered(reasonFromTrigger, cancelled);
        if (!cancelled || Interlocked.Exchange(ref engine._endHold, null) is not { } hold)
          return;
        engine.EndHeld.TrySetResult();
        hold.Task.Wait();
      }
    }
  }

  private static EngineCoordinator CreateCoordinator() =>
    new(nameof(ContextFanOutTest), NullLogger.Instance, useDispatcher: false);

  /// <summary>
  /// A coordinator that lets a test see the kick-start task of an engine, to wait until a run of it has ended.
  /// </summary>
  private sealed class InspectableCoordinator() : EngineCoordinator(nameof(ContextFanOutTest), NullLogger.Instance,
    useDispatcher: false)
  {
    public EngineTask KickStartOf(string name) => Mechanic.Tasks[EngineKickStartTask.KickStartName(name)];
  }

  private static EngineDescriptor Register(EngineCoordinator coordinator, string name, IEngine engine,
    Func<IEngineContext> context = null, string dependsOn = null)
  {
    var descriptor = coordinator.RegisterEngine(name, _ => Task.FromResult(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult(context?.Invoke() ?? new NoContext()));
    if (dependsOn != null)
      descriptor.AddDependsOn(dependsOn);
    return descriptor;
  }

  /// <summary>
  /// Registers <paramref name="session"/> on a new coordinator, without restarts, with a context taken from
  /// <paramref name="current"/> that fails for workspace-C once the test completes the returned gate, and starts it.
  /// </summary>
  private static async Task<(InspectableCoordinator coordinator, TaskCompletionSource contextForCEntered,
    TaskCompletionSource contextGate)> StartWithContextFailingForCAsync(HeldStartEngine session, Func<string> current)
  {
    var ct = TestContext.Current.CancellationToken;
    var contextGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var contextForCEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new InspectableCoordinator();
    coordinator.RegisterEngine("Session", _ => Task.FromResult<IEngine>(session), requiredForRunning: true)
      .SetContextFactory(async _ =>
      {
        var value = current();
        if (value == "workspace-C")
        {
          contextForCEntered.TrySetResult();
          await contextGate.Task;
          throw new InvalidOperationException("workspace-C cannot be opened");
        }
        return new ValueContext(value);
      })
      .WithoutRestarts();
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    return (coordinator, contextForCEntered, contextGate);
  }

  /// <summary>
  /// Sets up a session engine with two dependents and one independent engine, and starts them all.
  /// </summary>
  private static async Task<(EngineCoordinator coordinator, RecordingEngine session, RecordingEngine kernel,
    RecordingEngine assistant, RecordingEngine map)> StartHostAsync(Func<IEngineContext> sessionContext)
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var session = new RecordingEngine();
    var kernel = new RecordingEngine("Session");
    var assistant = new RecordingEngine("Session");
    var map = new RecordingEngine();
    Register(coordinator, "Session", session, sessionContext);
    Register(coordinator, "Kernel", kernel, dependsOn: "Session");
    Register(coordinator, "Assistant", assistant, dependsOn: "Session");
    Register(coordinator, "Map", map);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
    return (coordinator, session, kernel, assistant, map);
  }

  /// <summary>
  /// Verifies that the dependents load with the session's context, and the independent engine with its own.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_StartAsync_DependentsLoadWithTheSessionContext()
  {
    var (_, session, kernel, assistant, map) = await StartHostAsync(() => new ValueContext("workspace-A"));

    session.LoadedWith.Should().Equal("workspace-A");
    kernel.LoadedWith.Should().Equal("workspace-A");
    assistant.LoadedWith.Should().Equal("workspace-A");
    map.LoadedWith.Should().Equal("<none>");
  }

  /// <summary>
  /// Verifies that invalidating the session's context (the host opened another workspace) reloads both dependents
  /// with the context the factory now produces, leaves the independent engine alone, and ends Running again.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_Session_ReloadsAllDependentsWithTheNewContext()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var (coordinator, session, kernel, assistant, map) = await StartHostAsync(() => new ValueContext(current));

    current = "workspace-B";
    await coordinator.InvalidateContextForEngineAsync("Session", ct).WaitAsync(MustComplete, ct);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    session.LoadedWith.Should().Equal("workspace-A", "workspace-B");
    kernel.LoadedWith.Should().Equal("workspace-A", "workspace-B");
    assistant.LoadedWith.Should().Equal("workspace-A", "workspace-B");
    map.LoadedWith.Should().ContainSingle("the map does not depend on the session");
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
    coordinator.GetEngineIfReady("Kernel").Should().BeSameAs(kernel);
    coordinator.GetEngineIfReady("Assistant").Should().BeSameAs(assistant);
  }

  /// <summary>
  /// Verifies that refreshing the session with a context the host supplies pushes that context into the live
  /// session engine without reloading anything: the dependents read the new value through the session engine the
  /// next time they look, and the coordinator stays Running. (Reloading on a refresh is the engine's own decision,
  /// in its ContextChanged hook.)
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RefreshEngineContextAsync_Session_PushesTheContextWithoutReloading()
  {
    var ct = TestContext.Current.CancellationToken;
    var (coordinator, session, kernel, assistant, map) = await StartHostAsync(() => new ValueContext("workspace-A"));
    var supplied = new ValueContext("workspace-C");

    await coordinator.RefreshEngineContextAsync("Session", supplied, ct).WaitAsync(MustComplete, ct);

    session.Context.Should().BeSameAs(supplied);
    ((RecordingEngine)kernel.Context.GetUnderlyingEngine("Session")).Context.Should().BeSameAs(supplied);
    foreach (var engine in new[] { session, kernel, assistant, map })
    {
      engine.LoadedWith.Should().ContainSingle("a refresh does not reload");
      engine.State.Should().Be(EngineState.Ready);
    }
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Verifies that a context refresh for an engine named in a different case still gives the engine its underlying
  /// engines: engine names are case-insensitive everywhere, also where the refresh looks up the engine's dependencies.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RefreshEngineContextAsync_NameInOtherCase_KeepsTheUnderlyingEngines()
  {
    var ct = TestContext.Current.CancellationToken;
    var (coordinator, session, kernel, _, _) = await StartHostAsync(() => new ValueContext("workspace-A"));

    await coordinator.RefreshEngineContextAsync("KERNEL", new ValueContext("kernel-B"), ct).WaitAsync(MustComplete, ct);

    kernel.Context.Inner.Should().BeOfType<ValueContext>().Which.Value.Should().Be("kernel-B");
    kernel.Context.GetUnderlyingEngine("Session").Should().BeSameAs(session);
  }

  /// <summary>
  /// Verifies that invalidating one dependent reloads only that dependent: the session, its other dependent and
  /// the independent engine keep their loads.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_OneDependent_LeavesTheOthersLoaded()
  {
    var ct = TestContext.Current.CancellationToken;
    var (coordinator, session, kernel, assistant, map) = await StartHostAsync(() => new ValueContext("workspace-A"));

    await coordinator.InvalidateContextForEngineAsync("Kernel", ct).WaitAsync(MustComplete, ct);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    kernel.LoadedWith.Should().HaveCount(2);
    session.LoadedWith.Should().HaveCount(1);
    assistant.LoadedWith.Should().HaveCount(1);
    map.LoadedWith.Should().HaveCount(1);
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Verifies that switching the session twice in a row ends with every dependent loaded with the last context
  /// and nothing stuck in between.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_SessionTwice_EndsWithTheLastContext()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var (coordinator, session, kernel, assistant, _) = await StartHostAsync(() => new ValueContext(current));

    current = "workspace-B";
    var first = coordinator.InvalidateContextForEngineAsync("Session", ct);
    current = "workspace-C";
    var second = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await Task.WhenAll(first, second).WaitAsync(MustComplete, ct);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var last = new List<RecordingEngine> { session, kernel, assistant };
    last.Should().AllSatisfy(engine =>
    {
      engine.State.Should().Be(EngineState.Ready);
      engine.LoadedWith.Should().EndWith("workspace-C");
    });
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Verifies that an invalidation that arrives while the engine is still being started for the previous one (its
  /// kick-start has started it and waits for the start to end) restarts it with the new context: the kick-start's reset
  /// must restart the engine it is starting, not only one it has finished starting.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_WhileTheEngineStartsForThePreviousOne_RestartsIt()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var session = new HeldStartEngine();
    var coordinator = CreateCoordinator();
    Register(coordinator, "Session", session, () => new ValueContext(current));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var hold = session.HoldNextStart();

    current = "workspace-B";
    var first = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await Wait.UntilAsync(() => session.LoadedWith.Count == 2, MustComplete, "the engine loads for workspace-B");
    current = "workspace-C";
    var second = coordinator.InvalidateContextForEngineAsync("Session", ct);
    hold.SetResult();
    await Task.WhenAll(first, second).WaitAsync(MustComplete, ct);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    await Wait.UntilAsync(() => session.LoadedWith.Count == 3, MustComplete, "the engine loads again for workspace-C");
    session.LoadedWith.Should().Equal("workspace-A", "workspace-B", "workspace-C");
    session.State.Should().Be(EngineState.Ready);
  }

  /// <summary>
  /// Verifies that an invalidation that arrives while the engine is still Created, because the kick-start is starting it
  /// for the previous invalidation, restarts it with the new context. The start that was under way loads with the
  /// previous context; the engine must not stay loaded with it.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_WhileTheEngineIsCreatedForThePreviousOne_RestartsIt()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var contextGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var contextForCEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var session = new HeldStartEngine();
    var coordinator = new InspectableCoordinator();
    coordinator.RegisterEngine("Session", _ => Task.FromResult<IEngine>(session), requiredForRunning: true)
      .SetContextFactory(async _ =>
      {
        var value = current;
        if (value == "workspace-C")
        {
          contextForCEntered.TrySetResult();
          await contextGate.Task;
        }
        return new ValueContext(value);
      });
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var hold = session.HoldNextStart(beforeSetUp: true);

    current = "workspace-B";
    var first = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await session.StartHeld.Task.WaitAsync(MustComplete, ct);
    session.State.Should().Be(EngineState.Created, "the start for workspace-B has not set the engine up yet");
    var startingRun = coordinator.KickStartOf("Session").LatestRunEnded;

    current = "workspace-C";
    var second = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await contextForCEntered.Task.WaitAsync(MustComplete, ct);
    hold.SetResult();
    // WhenAny of one task: wait until it ends, without its exception.
    await Task.WhenAny(startingRun).WaitAsync(MustComplete, ct);
    // The ended run returns once the start does; the load it started may still be running.
    await Wait.UntilAsync(() => session.LoadedWith.Count == 2, MustComplete, "the start under way loads with workspace-B");
    session.LoadedWith.Should().Equal(["workspace-A", "workspace-B"], "the start under way loaded with workspace-B");
    contextGate.SetResult();
    await Task.WhenAll(first, second).WaitAsync(MustComplete, ct);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    await Wait.UntilAsync(() => session.LoadedWith.Count == 3, MustComplete, "the engine loads again for workspace-C");
    session.LoadedWith.Should().Equal("workspace-A", "workspace-B", "workspace-C");
    session.State.Should().Be(EngineState.Ready);
  }

  /// <summary>
  /// Verifies that a restart prepared by an invalidation is given up when the next kick-start fails without starting the
  /// engine (its context fails), also when the previous run, ended by that invalidation, comes back from its start after
  /// it. That run must not take the restart as carried out, or whoever waits until the engine is ready waits until it
  /// stops.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_ContextFailsAfterTheEndedRunReturns_GivesUpTheRestart()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var session = new HeldStartEngine();
    var (coordinator, contextForCEntered, contextGate) = await StartWithContextFailingForCAsync(session, () => current);
    var hold = session.HoldNextStart();

    current = "workspace-B";
    var first = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await session.StartHeld.Task.WaitAsync(MustComplete, ct);
    var startingRun = coordinator.KickStartOf("Session").LatestRunEnded;

    current = "workspace-C";
    var second = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await contextForCEntered.Task.WaitAsync(MustComplete, ct);
    hold.SetResult();
    await Task.WhenAny(startingRun).WaitAsync(MustComplete, ct);
    contextGate.SetResult();
    await Task.WhenAny(Task.WhenAll(first, second)).WaitAsync(MustComplete, ct);

    var ready = session.EnsureReadyAsync();
    await Wait.UntilAsync(() => ready.IsCompleted, MustComplete,
      "the restart is given up, so the wait for the engine ends");
    session.State.Should().Be(EngineState.Faulted);
  }

  /// <summary>
  /// Verifies that an engine ends Faulted when an invalidation finds it still Created, the next kick-start's context then
  /// fails, and only after that the ended run starts the engine with the previous context (#86 item 7). Without this,
  /// the engine stayed Ready on the previous context, which looks the same as a right answer.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_ContextFailsBeforeTheEndedRunStartsTheEngine_FaultsIt()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var session = new HeldStartEngine();
    var (coordinator, contextForCEntered, contextGate) = await StartWithContextFailingForCAsync(session, () => current);
    var kickStart = coordinator.KickStartOf("Session");
    var hold = session.HoldNextStart(beforeSetUp: true);

    current = "workspace-B";
    var first = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await session.StartHeld.Task.WaitAsync(MustComplete, ct);
    session.State.Should().Be(EngineState.Created, "the start for workspace-B has not set the engine up yet");
    var startingRun = kickStart.LatestRunEnded;

    current = "workspace-C";
    var second = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await contextForCEntered.Task.WaitAsync(MustComplete, ct);
    contextGate.SetResult();
    await Wait.UntilAsync(() => kickStart.State.IsCompleted(), MustComplete, "the run for workspace-C fails");
    session.LoadedWith.Should().Equal(["workspace-A"], "the ended run has not started the engine yet");
    hold.SetResult();
    await Task.WhenAny(startingRun).WaitAsync(MustComplete, ct);
    await Task.WhenAny(Task.WhenAll(first, second)).WaitAsync(MustComplete, ct);

    var ready = session.EnsureReadyAsync();
    await Wait.UntilAsync(() => ready.IsCompleted, MustComplete, "the engine is given up, so the wait for it ends");
    session.LoadedWith.Should().Equal("workspace-A", "workspace-B");
    session.State.Should().Be(EngineState.Faulted, "the engine was started with the previous context");
  }

  /// <summary>
  /// Verifies that an engine ends Faulted when an invalidation finds it still Created, the ended run then starts it with
  /// the previous context, and only after that the next kick-start's context fails (#86 item 7). Nothing restarts the
  /// engine then, so it must not stay Ready on the previous context.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_ContextFailsAfterTheEndedRunStartsTheEngine_FaultsIt()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var session = new HeldStartEngine();
    var (coordinator, contextForCEntered, contextGate) = await StartWithContextFailingForCAsync(session, () => current);
    var hold = session.HoldNextStart(beforeSetUp: true);

    current = "workspace-B";
    var first = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await session.StartHeld.Task.WaitAsync(MustComplete, ct);
    session.State.Should().Be(EngineState.Created, "the start for workspace-B has not set the engine up yet");
    var startingRun = coordinator.KickStartOf("Session").LatestRunEnded;

    current = "workspace-C";
    var second = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await contextForCEntered.Task.WaitAsync(MustComplete, ct);
    hold.SetResult();
    await Task.WhenAny(startingRun).WaitAsync(MustComplete, ct);
    await Wait.UntilAsync(() => session.State == EngineState.Ready, MustComplete,
      "the ended run starts the engine with workspace-B");
    contextGate.SetResult();
    await Task.WhenAny(Task.WhenAll(first, second)).WaitAsync(MustComplete, ct);

    await Wait.UntilAsync(() => session.State == EngineState.Faulted, MustComplete,
      "the engine started with the previous context is given up");
    var ready = session.EnsureReadyAsync();
    await Wait.UntilAsync(() => ready.IsCompleted, MustComplete, "the wait for the given-up engine ends");
    session.LoadedWith.Should().Equal("workspace-A", "workspace-B");
  }

  /// <summary>
  /// Verifies that the next kick-start run waits until the engine started with the previous context has been given up
  /// (#86 item 7). Otherwise a run for a new invalidation finds the engine still Ready on the previous context and
  /// completes, and the give-up then faults the engine under it: the kick-start and the engine disagree.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_AgainWhileTheEngineIsGivenUp_TheKickStartWaitsForTheGiveUp()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var session = new HeldStartEngine(withMonitor: true);
    var (coordinator, contextForCEntered, contextGate) = await StartWithContextFailingForCAsync(session, () => current);
    var kickStart = coordinator.KickStartOf("Session");
    var hold = session.HoldNextStart(beforeSetUp: true);

    // As in ...ContextFailsAfterTheEndedRunStartsTheEngine_FaultsIt, with the give-up held while it ends the tasks.
    current = "workspace-B";
    var first = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await session.StartHeld.Task.WaitAsync(MustComplete, ct);
    var startingRun = kickStart.LatestRunEnded;
    current = "workspace-C";
    var second = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await contextForCEntered.Task.WaitAsync(MustComplete, ct);
    hold.SetResult();
    await Task.WhenAny(startingRun).WaitAsync(MustComplete, ct);
    await Wait.UntilAsync(() => session.State == EngineState.Ready, MustComplete,
      "the ended run starts the engine with workspace-B");
    var endHold = session.HoldNextEnd();
    contextGate.SetResult();
    await session.EndHeld.Task.WaitAsync(MustComplete, ct);

    current = "workspace-D";
    var third = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await Task.WhenAny(Task.WhenAll(first, second, third)).WaitAsync(MustComplete, ct);
    // The run for workspace-D must not end while the give-up is held: it would find the engine as it was.
    await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
    kickStart.State.IsCompleted().Should().BeFalse("the run for workspace-D waits until the give-up has ended");
    endHold.SetResult();

    await Wait.UntilAsync(() => kickStart.State.IsCompleted(), MustComplete, "the run for workspace-D ends");
    var ready = session.EnsureReadyAsync();
    await Wait.UntilAsync(() => ready.IsCompleted, MustComplete, "the wait for the given-up engine ends");
    // Until #131: the run for workspace-D does not reload a given-up engine. The kick-start agreeing with the engine is
    // what this test is about, and holds either way.
    session.State.Should().Be(EngineState.Faulted, "the engine was given up, and nothing reloads it yet (#131)");
    kickStart.State.Should().NotBe(EngineTaskState.CompletedWithSuccess, "the kick-start agrees with the engine");
  }

  /// <summary>
  /// Verifies that an invalidation that arrives while a kick-start run restarts the engine before starting it (the
  /// restart an earlier reset prepared) restarts the engine again with the new context. That run then starts the engine
  /// with the previous context; it must not count as carrying out the restart the new invalidation prepared.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_WhileTheNextRunRestartsTheEngine_RestartsItForTheNewContext()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var contextGateC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var contextForCEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var contextGateD = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var contextForDEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var restartsLogged = 0;
    var session = new HeldStartEngine(withMonitor: true,
      new OnMessageLogger("Ending the current tasks", () => Interlocked.Increment(ref restartsLogged)));
    var coordinator = new InspectableCoordinator();
    coordinator.RegisterEngine("Session", _ => Task.FromResult<IEngine>(session), requiredForRunning: true)
      .SetContextFactory(async _ =>
      {
        var value = current;
        if (value == "workspace-C")
        {
          contextForCEntered.TrySetResult();
          await contextGateC.Task;
        }
        else if (value == "workspace-D")
        {
          contextForDEntered.TrySetResult();
          await contextGateD.Task;
        }
        return new ValueContext(value);
      });
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var kickStart = coordinator.KickStartOf("Session");

    // As in ...WhileTheEngineIsCreatedForThePreviousOne_RestartsIt: the run for workspace-C finds the engine started
    // with workspace-B by the run that the invalidation to workspace-C ended, and restarts it.
    var hold = session.HoldNextStart(beforeSetUp: true);
    current = "workspace-B";
    var first = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await session.StartHeld.Task.WaitAsync(MustComplete, ct);
    var startingRun = kickStart.LatestRunEnded;
    current = "workspace-C";
    var second = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await contextForCEntered.Task.WaitAsync(MustComplete, ct);
    hold.SetResult();
    await Task.WhenAny(startingRun).WaitAsync(MustComplete, ct);

    // The run for workspace-C is held in that restart, before it starts the engine.
    var endHold = session.HoldNextEnd();
    contextGateC.SetResult();
    await session.EndHeld.Task.WaitAsync(MustComplete, ct);
    kickStart.State.Should().Be(EngineTaskState.Running);
    var runForC = kickStart.LatestRunEnded;
    var restartsBefore = Volatile.Read(ref restartsLogged);

    // The invalidation to workspace-D resets the kick-start meanwhile: it is done with it once the task is reset, or
    // once it ends the engine's tasks for a restart (it then waits for the held end).
    current = "workspace-D";
    var third = coordinator.InvalidateContextForEngineAsync("Session", ct);
    await Wait.UntilAsync(
      () => kickStart.State != EngineTaskState.Running || Volatile.Read(ref restartsLogged) > restartsBefore,
      MustComplete, "the invalidation to workspace-D resets the kick-start");
    endHold.SetResult();
    await Task.WhenAny(runForC).WaitAsync(MustComplete, ct);
    await contextForDEntered.Task.WaitAsync(MustComplete, ct);
    contextGateD.SetResult();
    await Task.WhenAll(first, second, third).WaitAsync(MustComplete, ct);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    await Wait.UntilAsync(() => session.LoadedWith.LastOrDefault() == "workspace-D", MustComplete,
      "the engine loads again for workspace-D");
    session.State.Should().Be(EngineState.Ready);
  }
}
