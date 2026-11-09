using System;
using System.Data;
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
/// Contains tests for engine start-up: the kick-start must wait until an engine has loaded, since server hosts
/// rely on the coordinator (and the blocking engine lookup) to hold queries until the data is complete.
/// </summary>
public class EngineStartupTest
{
  /// <summary>
  /// Time given to a call that must NOT complete. Without the fix, the calls under test complete in milliseconds.
  /// </summary>
  private static readonly TimeSpan MustStayPending = TimeSpan.FromMilliseconds(500);

  /// <summary>
  /// Time given to a call that must complete.
  /// </summary>
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Engine shaped like typical host engines: a quick first task, then a load that blocks until the test releases
  /// a gate (or fails, if the gate is faulted).
  /// </summary>
  private sealed class SlowLoadEngine(Task loadGate) : BaseEngine(NullLogger.Instance)
  {
    private int _startCalls;

    public int StartCalls => Volatile.Read(ref _startCalls);

    public override Task StartAsync()
    {
      Interlocked.Increment(ref _startCalls);
      return base.StartAsync();
    }

    protected override void SetUpTasks()
    {
      var license = CreateTask("ValidateLicense", _ => Task.CompletedTask);
      var load = CreateTask("InitializeConnections", async _ => await loadGate.ConfigureAwait(false));
      load.AddDependsOn(license);
    }
  }

  /// <summary>
  /// Creates a coordinator the way a host without a dispatcher does.
  /// </summary>
  private static EngineCoordinator CreateCoordinator() =>
    new("EngineStartupTest", NullLogger.Instance, useDispatcher: false);

  private static void Register(EngineCoordinator coordinator, string name, IEngine engine, string dependsOn = null)
  {
    var descriptor = coordinator.RegisterEngine(name, _ => Task.FromResult(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      // A failed load must fault at once, not retry by the default restart policy.
      .WithoutRestarts();
    if (dependsOn != null)
      descriptor.AddDependsOn(dependsOn);
  }

  /// <summary>
  /// Verifies coordinator ensure ready waits until a required engine has finished loading.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureReadyAsync_WaitsUntilEngineHasLoaded()
  {
    var ct = TestContext.Current.CancellationToken;
    var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = new SlowLoadEngine(loadGate.Task);
    var coordinator = CreateCoordinator();
    Register(coordinator, "E", engine);

    var ensureReady = coordinator.EnsureReadyAsync(ct);
    var completedEarly = await Task.WhenAny(ensureReady, Task.Delay(MustStayPending, ct)) == ensureReady;
    var stateWhileLoading = engine.State;
    loadGate.SetResult();

    completedEarly.Should().BeFalse("the engine was still loading (engine state {0})", stateWhileLoading);
    await ensureReady.WaitAsync(MustComplete, ct);
    engine.State.Should().Be(EngineState.Ready);
    coordinator.GetEngineIfReady("E").Should().BeSameAs(engine);
  }

  /// <summary>
  /// Verifies the blocking engine lookup (used by server hosts) waits for the engine and returns it once loaded.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_GetEngineAsync_BlocksUntilEngineHasLoaded()
  {
    var ct = TestContext.Current.CancellationToken;
    var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = new SlowLoadEngine(loadGate.Task);
    var coordinator = CreateCoordinator();
    Register(coordinator, "E", engine);

    // Start first, as a host lookup does, so the engine exists when the blocking lookup runs.
    await coordinator.StartAsync();
    SpinWait.SpinUntil(() => coordinator.Engines.ContainsKey("E"), MustComplete).Should().BeTrue();

    var getEngine = coordinator.GetEngineAsync("E", throwErrorIfNotFound: true, token: ct);
    var completedEarly = await Task.WhenAny(getEngine, Task.Delay(MustStayPending, ct)) == getEngine;
    loadGate.SetResult();

    completedEarly.Should().BeFalse("the engine was still loading");
    (await getEngine.WaitAsync(MustComplete, ct)).Should().BeSameAs(engine);
  }

  /// <summary>
  /// Verifies that waiting for a loading engine does not hang when that engine fails: ensure ready returns and the
  /// blocking lookup returns no engine.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureReadyAsync_ReturnsWhenEngineFailsToLoad()
  {
    var ct = TestContext.Current.CancellationToken;
    var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var engine = new SlowLoadEngine(loadGate.Task);
    var coordinator = CreateCoordinator();
    Register(coordinator, "E", engine);

    var ensureReady = coordinator.EnsureReadyAsync(ct);
    await Task.Delay(MustStayPending, ct);
    loadGate.SetException(new InvalidOperationException("Load failed."));

    await ensureReady.WaitAsync(MustComplete, ct);
    engine.State.Should().Be(EngineState.Faulted);
    (await coordinator.GetEngineAsync("E", throwErrorIfNotFound: true, token: ct).WaitAsync(MustComplete, ct)).Should().BeNull();
  }

  /// <summary>
  /// Verifies that an engine depending on another engine (for example an overlay on its underlying network) is only
  /// started once the engine it depends on has finished loading.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_DependentEngine_StartsOnlyAfterUnderlyingEngineHasLoaded()
  {
    var ct = TestContext.Current.CancellationToken;
    var underlyingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var underlying = new SlowLoadEngine(underlyingGate.Task);
    var overlay = new SlowLoadEngine(Task.CompletedTask);
    var coordinator = CreateCoordinator();
    Register(coordinator, "E", underlying);
    Register(coordinator, "E-DP", overlay, dependsOn: "E");

    var ensureReady = coordinator.EnsureReadyAsync(ct);
    await Task.Delay(MustStayPending, ct);
    var overlayStartsWhileUnderlyingLoads = overlay.StartCalls;
    underlyingGate.SetResult();

    overlayStartsWhileUnderlyingLoads.Should().Be(0, "the underlying engine was still loading");
    await ensureReady.WaitAsync(MustComplete, ct);
    underlying.State.Should().Be(EngineState.Ready);
    overlay.State.Should().Be(EngineState.Ready);
  }

  /// <summary>
  /// Verifies one server-host engine lookup on a coordinator that has not been started yet waits for the engine to
  /// be created and loaded, instead of throwing because the creation task has not run yet. Mirrors
  /// a host lookup that starts the coordinator in the background (RequestReadiness(false)) and
  /// then blocks on GetEngineAsync, so two starts race on the cold coordinator.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EngineForLookup_OnColdCoordinator_WaitsForCreationAndLoad()
  {
    const int rounds = 10;
    var pendingWindow = TimeSpan.FromMilliseconds(100);
    var ct = TestContext.Current.CancellationToken;
    for (var round = 0; round < rounds; round++)
    {
      var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var engine = new SlowLoadEngine(loadGate.Task);
      var coordinator = CreateCoordinator();
      Register(coordinator, "E", engine);

      _ = Task.Run(() => coordinator.EnsureReadyAsync(ct), ct);
      var getEngine = coordinator.GetEngineAsync("E", throwErrorIfNotFound: true, token: ct);
      var completedEarly = await Task.WhenAny(getEngine, Task.Delay(pendingWindow, ct)) == getEngine;
      var failure = getEngine.IsFaulted ? getEngine.Exception?.InnerException?.Message : null;
      loadGate.SetResult();

      failure.Should().BeNull("in round {0} the engine is registered, only not created yet", round);
      completedEarly.Should().BeFalse("in round {0} the engine was still loading", round);
      (await getEngine.WaitAsync(MustComplete, ct)).Should().BeSameAs(engine, "in round {0}", round);
    }
  }

  /// <summary>
  /// Verifies concurrent blocking lookups on a cold coordinator (the way parallel queries from a host arrive)
  /// all wait and all get the engine, and none fails while the coordinator is being started.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_ConcurrentGetEngineAsync_OnColdCoordinator_AllGetTheEngine()
  {
    const int rounds = 20;
    const int callers = 8;
    var ct = TestContext.Current.CancellationToken;
    for (var round = 0; round < rounds; round++)
    {
      var loadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var engine = new SlowLoadEngine(loadGate.Task);
      var coordinator = CreateCoordinator();
      Register(coordinator, "E", engine);

      // Dedicated threads, so the blocked callers do not starve the thread pool the engine's own tasks run on.
      using var barrier = new Barrier(callers);
      var lookups = Enumerable.Range(0, callers)
        .Select(_ => Task.Factory.StartNew(() =>
        {
          barrier.SignalAndWait(ct);
          // Synchronous, like a host lookup on a server.
          return coordinator.GetEngineAsync("E", throwErrorIfNotFound: true, token: ct).GetAwaiter().GetResult();
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default))
        .ToArray();
      await Task.Delay(100, ct);
      loadGate.SetResult();

      var results = await Task.WhenAll(lookups).WaitAsync(MustComplete, ct);
      results.Should().AllSatisfy(result => result.Should().BeSameAs(engine), "in round {0}", round);
      engine.StartCalls.Should().Be(1, "in round {0} the engine must be started exactly once", round);
    }
  }

  /// <summary>
  /// Verifies that asking for an engine that is not registered still throws.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_GetEngineAsync_UnknownEngine_Throws()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    Register(coordinator, "E", new SlowLoadEngine(Task.CompletedTask));

    // The overload a host lookup typically uses. Note GetEngineAsync(name, CancellationToken) is a different
    // overload (infinite time-out) that returns null for an unknown engine instead of throwing.
    var lookup = () => coordinator.GetEngineAsync("X", throwErrorIfNotFound: true, token: ct);

    await lookup.Should().ThrowAsync<EvaluateException>();
  }
}
