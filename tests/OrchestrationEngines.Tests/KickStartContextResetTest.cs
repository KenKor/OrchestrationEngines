using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the kick-start's restart of an engine whose context is computed again while a run of the
/// kick-start is still starting it (#85), and for its give-up when the next run fails without starting it (#86 item 7).
/// They reset the context task through <see cref="ContextResettingCoordinator"/> (#232).
/// </summary>
public class KickStartContextResetTest
{
  /// <summary>
  /// An engine that records the value of its context on each set-up (<see cref="SetUpWith"/>) and on each load (<see
  /// cref="LoadedWith"/>), and whose start, once armed, ends only when the test lets it. Held after the set-up, its
  /// load has run by then, but the kick-start that started it is still waiting for the start. Held before the set-up,
  /// the engine is still Created while the kick-start starts it.
  /// </summary>
  private sealed class HeldStartEngine : BaseEngine
  {
    private readonly ConcurrentQueue<string> _loadedWith = new();
    private readonly ConcurrentQueue<string> _setUpWith = new();
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

    /// <summary>
    /// The context of every set-up of the engine's tasks, in order. Unlike <see cref="LoadedWith"/>, it does not depend
    /// on the Load task having run: an engine given up right after its start may stop that task before it runs.
    /// </summary>
    public IReadOnlyCollection<string> SetUpWith => _setUpWith;

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
      _setUpWith.Enqueue(loadWith);
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
      private protected override void OnFailedEntered(string reasonFromTrigger = null, bool cancelled = false)
      {
        base.OnFailedEntered(reasonFromTrigger, cancelled);
        if (!cancelled || Interlocked.Exchange(ref engine._endHold, null) is not { } hold)
          return;
        engine.EndHeld.TrySetResult();
        hold.Task.Wait();
      }
    }
  }

  /// <summary>
  /// Registers <paramref name="session"/> on a new coordinator with a context taken from <paramref name="current"/>,
  /// held for workspace-C until the test completes the returned gate, and starts it.
  /// </summary>
  /// <param name="session">The engine.</param>
  /// <param name="current">The workspace the next context is computed for.</param>
  /// <param name="contextForCFails">The context for workspace-C then fails, and the engine is registered without
  /// restarts; otherwise that context is computed.</param>
  private static async Task<(ContextResettingCoordinator coordinator, TaskCompletionSource contextForCEntered,
    TaskCompletionSource contextGate)> StartWithContextHeldForCAsync(HeldStartEngine session, Func<string> current,
    bool contextForCFails = true)
  {
    var ct = TestContext.Current.CancellationToken;
    var contextGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var contextForCEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new ContextResettingCoordinator(nameof(KickStartContextResetTest));
    var descriptor = coordinator
      .RegisterEngine("Session", _ => Task.FromResult<IEngine>(session), requiredForRunning: true)
      .SetContextFactory(async _ =>
      {
        var value = current();
        if (value == "workspace-C")
        {
          contextForCEntered.TrySetResult();
          await contextGate.Task;
          if (contextForCFails)
            throw new InvalidOperationException("workspace-C cannot be opened");
        }
        return new ValueContext(value);
      });
    if (contextForCFails)
      descriptor.WithoutRestarts();
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    return (coordinator, contextForCEntered, contextGate);
  }

  /// <summary>
  /// Verifies that a context reset that arrives while the engine is still being started for the previous one (its
  /// kick-start has started it and waits for the start to end) restarts it with the new context: the kick-start's reset
  /// must restart the engine it is starting, not only one it has finished starting.
  /// </summary>
  [Fact]
  public async Task KickStart_ContextReset_WhileTheEngineStartsForThePreviousOne_RestartsIt()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var session = new HeldStartEngine();
    var coordinator = new ContextResettingCoordinator(nameof(KickStartContextResetTest));
    coordinator.RegisterEngine("Session", _ => Task.FromResult<IEngine>(session), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new ValueContext(current)));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var hold = session.HoldNextStart();

    current = "workspace-B";
    var first = coordinator.ResetContextAsync("Session");
    await Wait.UntilAsync(() => session.LoadedWith.Count == 2, MustComplete, "the engine loads for workspace-B");
    current = "workspace-C";
    var second = coordinator.ResetContextAsync("Session");
    hold.SetResult();
    await Task.WhenAll(first, second).WaitAsync(MustComplete, ct);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    await Wait.UntilAsync(() => session.LoadedWith.Count == 3, MustComplete, "the engine loads again for workspace-C");
    session.LoadedWith.Should().Equal("workspace-A", "workspace-B", "workspace-C");
    session.State.Should().Be(EngineState.Ready);
  }

  /// <summary>
  /// Verifies that a context reset that arrives while the engine is still Created, because the kick-start is starting
  /// it for the previous context reset, restarts it with the new context. The start that was under way loads with the
  /// previous context; the engine must not stay loaded with it.
  /// </summary>
  [Fact]
  public async Task KickStart_ContextReset_WhileTheEngineIsCreatedForThePreviousOne_RestartsIt()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var session = new HeldStartEngine();
    var (coordinator, contextForCEntered, contextGate) =
      await StartWithContextHeldForCAsync(session, () => current, contextForCFails: false);
    var hold = session.HoldNextStart(beforeSetUp: true);

    current = "workspace-B";
    var first = coordinator.ResetContextAsync("Session");
    await session.StartHeld.Task.WaitAsync(MustComplete, ct);
    session.State.Should().Be(EngineState.Created, "the start for workspace-B has not set the engine up yet");
    var startingRun = coordinator.KickStartOf("Session").LatestRunEnded;

    current = "workspace-C";
    var second = coordinator.ResetContextAsync("Session");
    await contextForCEntered.Task.WaitAsync(MustComplete, ct);
    hold.SetResult();
    // WhenAny of one task: wait until it ends, without its exception.
    await Task.WhenAny(startingRun).WaitAsync(MustComplete, ct);
    // The ended run returns once the start does; the load it started may still be running.
    await Wait.UntilAsync(() => session.LoadedWith.Count == 2, MustComplete,
      "the start under way loads with workspace-B");
    session.LoadedWith.Should().Equal(["workspace-A", "workspace-B"], "the start under way loaded with workspace-B");
    contextGate.SetResult();
    await Task.WhenAll(first, second).WaitAsync(MustComplete, ct);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    await Wait.UntilAsync(() => session.LoadedWith.Count == 3, MustComplete, "the engine loads again for workspace-C");
    session.LoadedWith.Should().Equal("workspace-A", "workspace-B", "workspace-C");
    session.State.Should().Be(EngineState.Ready);
  }

  /// <summary>
  /// Verifies that a restart prepared by a context reset is given up when the next kick-start fails without starting
  /// the engine (its context fails), also when the previous run, ended by that context reset, comes back from its start
  /// after it. That run must not take the restart as carried out, or whoever waits until the engine is ready waits
  /// until it stops.
  /// </summary>
  [Fact]
  public async Task KickStart_ContextReset_ContextFailsAfterTheEndedRunReturns_GivesUpTheRestart()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var session = new HeldStartEngine();
    var (coordinator, contextForCEntered, contextGate) = await StartWithContextHeldForCAsync(session, () => current);
    var hold = session.HoldNextStart();

    current = "workspace-B";
    var first = coordinator.ResetContextAsync("Session");
    await session.StartHeld.Task.WaitAsync(MustComplete, ct);
    var startingRun = coordinator.KickStartOf("Session").LatestRunEnded;

    current = "workspace-C";
    var second = coordinator.ResetContextAsync("Session");
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
  /// Verifies that an engine ends Faulted when a context reset finds it still Created, the next kick-start's context
  /// then fails, and only after that the ended run starts the engine with the previous context (#86 item 7). Without
  /// this, the engine stayed Ready on the previous context, which looks the same as a right answer.
  /// </summary>
  [Fact]
  public async Task KickStart_ContextReset_ContextFailsBeforeTheEndedRunStartsTheEngine_FaultsIt()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var session = new HeldStartEngine();
    var (coordinator, contextForCEntered, contextGate) = await StartWithContextHeldForCAsync(session, () => current);
    var kickStart = coordinator.KickStartOf("Session");
    var hold = session.HoldNextStart(beforeSetUp: true);

    current = "workspace-B";
    var first = coordinator.ResetContextAsync("Session");
    await session.StartHeld.Task.WaitAsync(MustComplete, ct);
    session.State.Should().Be(EngineState.Created, "the start for workspace-B has not set the engine up yet");
    var startingRun = kickStart.LatestRunEnded;

    current = "workspace-C";
    var second = coordinator.ResetContextAsync("Session");
    await contextForCEntered.Task.WaitAsync(MustComplete, ct);
    contextGate.SetResult();
    await Wait.UntilAsync(() => kickStart.State.IsCompleted(), MustComplete, "the run for workspace-C fails");
    session.LoadedWith.Should().Equal(["workspace-A"], "the ended run has not started the engine yet");
    hold.SetResult();
    await Task.WhenAny(startingRun).WaitAsync(MustComplete, ct);
    await Task.WhenAny(Task.WhenAll(first, second)).WaitAsync(MustComplete, ct);

    var ready = session.EnsureReadyAsync();
    await Wait.UntilAsync(() => ready.IsCompleted, MustComplete, "the engine is given up, so the wait for it ends");
    // The engine is given up as soon as its start returns, so its Load task for workspace-B may not have run yet: check
    // the set-up instead.
    session.SetUpWith.Should().Equal("workspace-A", "workspace-B");
    session.State.Should().Be(EngineState.Faulted, "the engine was started with the previous context");
  }

  /// <summary>
  /// Verifies that an engine ends Faulted when a context reset finds it still Created, the ended run then starts it
  /// with the previous context, and only after that the next kick-start's context fails (#86 item 7). Nothing restarts
  /// the engine then, so it must not stay Ready on the previous context.
  /// </summary>
  [Fact]
  public async Task KickStart_ContextReset_ContextFailsAfterTheEndedRunStartsTheEngine_FaultsIt()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var session = new HeldStartEngine();
    var (coordinator, contextForCEntered, contextGate) = await StartWithContextHeldForCAsync(session, () => current);
    var hold = session.HoldNextStart(beforeSetUp: true);

    current = "workspace-B";
    var first = coordinator.ResetContextAsync("Session");
    await session.StartHeld.Task.WaitAsync(MustComplete, ct);
    session.State.Should().Be(EngineState.Created, "the start for workspace-B has not set the engine up yet");
    var startingRun = coordinator.KickStartOf("Session").LatestRunEnded;

    current = "workspace-C";
    var second = coordinator.ResetContextAsync("Session");
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
  /// (#86 item 7). Otherwise a run for a new context reset finds the engine still Ready on the previous context and
  /// completes, and the give-up then faults the engine under it: the kick-start and the engine disagree.
  /// </summary>
  [Fact]
  public async Task KickStart_ContextReset_AgainWhileTheEngineIsGivenUp_TheKickStartWaitsForTheGiveUp()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = "workspace-A";
    var session = new HeldStartEngine(withMonitor: true);
    var (coordinator, contextForCEntered, contextGate) = await StartWithContextHeldForCAsync(session, () => current);
    var kickStart = coordinator.KickStartOf("Session");
    var hold = session.HoldNextStart(beforeSetUp: true);

    // As in ...ContextFailsAfterTheEndedRunStartsTheEngine_FaultsIt, with the give-up held while it ends the tasks.
    current = "workspace-B";
    var first = coordinator.ResetContextAsync("Session");
    await session.StartHeld.Task.WaitAsync(MustComplete, ct);
    var startingRun = kickStart.LatestRunEnded;
    current = "workspace-C";
    var second = coordinator.ResetContextAsync("Session");
    await contextForCEntered.Task.WaitAsync(MustComplete, ct);
    hold.SetResult();
    await Task.WhenAny(startingRun).WaitAsync(MustComplete, ct);
    await Wait.UntilAsync(() => session.State == EngineState.Ready, MustComplete,
      "the ended run starts the engine with workspace-B");
    var endHold = session.HoldNextEnd();
    contextGate.SetResult();
    await session.EndHeld.Task.WaitAsync(MustComplete, ct);

    current = "workspace-D";
    var third = coordinator.ResetContextAsync("Session");
    await Task.WhenAny(Task.WhenAll(first, second, third)).WaitAsync(MustComplete, ct);
    // The run for workspace-D must not end while the give-up is held: it would find the engine as it was.
    await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
    kickStart.State.IsCompleted().Should().BeFalse("the run for workspace-D waits until the give-up has ended");
    endHold.SetResult();

    await Wait.UntilAsync(() => kickStart.State.IsCompleted(), MustComplete, "the run for workspace-D ends");
    var ready = session.EnsureReadyAsync();
    await Wait.UntilAsync(() => ready.IsCompleted, MustComplete, "the wait for the given-up engine ends");
    // The run for workspace-D does not reload a given-up engine (#131 closed with the invalidation, #226). The
    // kick-start agreeing with the engine is what this test is about.
    session.State.Should().Be(EngineState.Faulted, "the engine was given up, and a context reset does not reload it");
    kickStart.State.Should().NotBe(EngineTaskState.CompletedWithSuccess, "the kick-start agrees with the engine");
  }

  /// <summary>
  /// Verifies that a context reset that arrives while a kick-start run restarts the engine before starting it (the
  /// restart an earlier reset prepared) restarts the engine again with the new context. That run then starts the engine
  /// with the previous context; it must not count as carrying out the restart the new context reset prepared.
  /// </summary>
  [Fact]
  public async Task KickStart_ContextReset_WhileTheNextRunRestartsTheEngine_RestartsItForTheNewContext()
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
    var coordinator = new ContextResettingCoordinator(nameof(KickStartContextResetTest));
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
    // with workspace-B by the run that the context reset to workspace-C ended, and restarts it.
    var hold = session.HoldNextStart(beforeSetUp: true);
    current = "workspace-B";
    var first = coordinator.ResetContextAsync("Session");
    await session.StartHeld.Task.WaitAsync(MustComplete, ct);
    var startingRun = kickStart.LatestRunEnded;
    current = "workspace-C";
    var second = coordinator.ResetContextAsync("Session");
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

    // The context reset to workspace-D resets the kick-start meanwhile: it is done with it once the task is reset, or
    // once it ends the engine's tasks for a restart (it then waits for the held end).
    current = "workspace-D";
    var third = coordinator.ResetContextAsync("Session");
    await Wait.UntilAsync(
      () => kickStart.State != EngineTaskState.Running || Volatile.Read(ref restartsLogged) > restartsBefore,
      MustComplete, "the context reset to workspace-D resets the kick-start");
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
