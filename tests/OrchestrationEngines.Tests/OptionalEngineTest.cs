using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for engines registered with <c>requiredForRunning: false</c>: an optional engine (for example an
/// assistant or a scripting kernel next to the core data engines) must not hold up or break the application. The
/// coordinator is Running once the required engines are Ready, whatever the optional ones are doing.
/// </summary>
public class OptionalEngineTest
{
  private static readonly TimeSpan MustStayPending = TimeSpan.FromMilliseconds(500);

  /// <summary>
  /// An engine with one load task that waits for a gate the test controls.
  /// </summary>
  private sealed class GatedEngine(Func<Task> loadGate) : BaseEngine(NullLogger.Instance)
  {
    public GatedEngine(Task loadGate)
      : this(() => loadGate)
    {
    }

    protected override void SetUpTasks() => CreateTask("Load", async _ => await loadGate().ConfigureAwait(false));
  }

  private static EngineCoordinator CreateCoordinator(ILogger logger = null) =>
    new(nameof(OptionalEngineTest), logger ?? NullLogger.Instance, useDispatcher: false);

  private static void Register(EngineCoordinator coordinator, string name, IEngine engine, bool required)
  {
    coordinator.RegisterEngine(name, _ => Task.FromResult(engine), requiredForRunning: required)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();
  }

  /// <summary>
  /// Verifies that ensure ready does not wait for an optional engine, and that the coordinator reports Running while
  /// that engine is still loading.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureReadyAsync_OptionalEngineStillLoading_IsRunning()
  {
    var ct = TestContext.Current.CancellationToken;
    var optionalGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var core = new GatedEngine(Task.CompletedTask);
    var optional = new GatedEngine(optionalGate.Task);
    var coordinator = CreateCoordinator();
    Register(coordinator, "Core", core, required: true);
    Register(coordinator, "Optional", optional, required: false);

    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var stateWhileOptionalLoads = coordinator.State;
    optionalGate.SetResult();

    core.State.Should().Be(EngineState.Ready);
    stateWhileOptionalLoads.Should().Be(EngineCoordinatorState.Running, "only the required engines decide");
    await Wait.UntilAsync(() => coordinator.GetEngineIfReady("Optional") != null,
      MustComplete, "the optional engine finishes loading");
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Verifies that an optional engine that fails to load leaves the coordinator Running and the required engines
  /// Ready, and that asking for the failed engine returns null instead of waiting.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_OptionalEngineFailsToLoad_StaysRunning()
  {
    var ct = TestContext.Current.CancellationToken;
    var optionalGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var core = new GatedEngine(Task.CompletedTask);
    var optional = new GatedEngine(optionalGate.Task);
    var coordinator = CreateCoordinator();
    Register(coordinator, "Core", core, required: true);
    Register(coordinator, "Optional", optional, required: false);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    optionalGate.SetException(new InvalidOperationException("The optional engine cannot load."));
    await Wait.UntilAsync(() => optional.State == EngineState.Faulted, MustComplete, "the optional engine fails");

    core.State.Should().Be(EngineState.Ready);
    coordinator.State.Should().Be(EngineCoordinatorState.Running, "an optional engine's failure is not the application's");
    (await coordinator.GetEngineAsync("Optional", throwErrorIfNotFound: true, token: ct).WaitAsync(MustComplete, ct)).Should().BeNull();
    (await coordinator.GetEngineAsync("Core", throwErrorIfNotFound: true, token: ct).WaitAsync(MustComplete, ct)).Should().BeSameAs(core);
  }

  /// <summary>
  /// Verifies that refreshing an engine's context while an optional engine has failed returns the coordinator to
  /// Running: the refresh path has its own readiness check, which must look at the required engines only too.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RefreshEngineContextAsync_OptionalEngineFailed_ReturnsToRunning()
  {
    var ct = TestContext.Current.CancellationToken;
    var core = new GatedEngine(Task.CompletedTask);
    var optional = new GatedEngine(Task.FromException(new InvalidOperationException("The optional engine cannot load.")));
    var coordinator = CreateCoordinator();
    Register(coordinator, "Core", core, required: true);
    Register(coordinator, "Optional", optional, required: false);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await Wait.UntilAsync(() => optional.State == EngineState.Faulted, MustComplete, "the optional engine fails");
    coordinator.State.Should().Be(EngineCoordinatorState.Running);

    await coordinator.RefreshEngineContextAsync("Core", new NoContext(), ct).WaitAsync(MustComplete, ct);

    coordinator.State.Should().Be(EngineCoordinatorState.Running);
    core.State.Should().Be(EngineState.Ready);
  }

  /// <summary>
  /// Verifies that invalidating an optional engine's context returns the coordinator to Running: no required engine
  /// changes state, so no mechanic status event brings the coordinator back from Initializing.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_OptionalEngine_ReturnsToRunning()
  {
    var ct = TestContext.Current.CancellationToken;
    var core = new GatedEngine(Task.CompletedTask);
    var optional = new GatedEngine(Task.CompletedTask);
    var coordinator = CreateCoordinator();
    Register(coordinator, "Core", core, required: true);
    Register(coordinator, "Optional", optional, required: false);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await Wait.UntilAsync(() => optional.State == EngineState.Ready, MustComplete, "the optional engine loads");

    await coordinator.InvalidateContextForEngineAsync("Optional", ct).WaitAsync(MustComplete, ct);

    await Wait.UntilAsync(() => coordinator.State == EngineCoordinatorState.Running, MustComplete,
      "the required engines are still Ready");
    await Wait.UntilAsync(() => optional.State == EngineState.Ready, MustComplete, "the optional engine loads again");
  }

  /// <summary>
  /// Verifies that an invalidation the mechanic rejects (an unknown engine) leaves the coordinator Running.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_UnknownEngine_ThrowsAndStaysRunning()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    Register(coordinator, "Core", new GatedEngine(Task.CompletedTask), required: true);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var act = () => coordinator.InvalidateContextForEngineAsync("Unknown", ct).WaitAsync(MustComplete, ct);

    await act.Should().ThrowAsync<ArgumentException>();
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Verifies that the coordinator still waits for a required engine when an optional engine loaded first: optional
  /// engines must not make the coordinator Running early either.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_EnsureReadyAsync_RequiredEngineStillLoading_WaitsEvenWhenOptionalIsReady()
  {
    var ct = TestContext.Current.CancellationToken;
    var coreGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var core = new GatedEngine(coreGate.Task);
    var optional = new GatedEngine(Task.CompletedTask);
    var coordinator = CreateCoordinator();
    Register(coordinator, "Core", core, required: true);
    Register(coordinator, "Optional", optional, required: false);

    var ensureReady = coordinator.EnsureReadyAsync(ct);
    await Wait.UntilAsync(() => optional.State == EngineState.Ready, MustComplete, "the optional engine loads at once");
    var completedEarly = await Task.WhenAny(ensureReady, Task.Delay(MustStayPending, ct)) == ensureReady;
    var stateWhileCoreLoads = coordinator.State;
    coreGate.SetResult();

    completedEarly.Should().BeFalse("the required engine was still loading");
    stateWhileCoreLoads.Should().NotBe(EngineCoordinatorState.Running);
    await ensureReady.WaitAsync(MustComplete, ct);
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Verifies that without the dispatcher an invalidation of an optional engine that ends while an invalidation of a
  /// required engine is in flight does not bring the coordinator back to Running: it stays Initializing until the
  /// required engine has loaded again.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_OverlappingWithoutDispatcher_NotRunningWhileRequiredReloads()
  {
    var ct = TestContext.Current.CancellationToken;
    var optionalAtReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var requiredFired = new ManualResetEventSlim();
    var optionalDone = new ManualResetEventSlim();
    var reloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var loads = 0;
    // The optional engine's call waits, after its trigger, until the required engine's call has fired its own; the
    // required engine's call then waits, before its reset, until the optional engine's call has ended.
    var logger = new OnMessageLogger("depending on and incl. Optional.", () =>
      {
        optionalAtReset.TrySetResult();
        requiredFired.Wait(MustComplete).Should().BeTrue("the required engine's call fires its trigger");
      },
      new OnMessageLogger("depending on and incl. Required.", () =>
      {
        requiredFired.Set();
        optionalDone.Wait(MustComplete).Should().BeTrue("the optional engine's call ends");
      }));
    var coordinator = CreateCoordinator(logger);
    var optionalEngine = new GatedEngine(Task.CompletedTask);
    var requiredEngine = new GatedEngine(() =>
      Interlocked.Increment(ref loads) == 1 ? Task.CompletedTask : reloadGate.Task);
    Register(coordinator, "Required", requiredEngine, required: true);
    Register(coordinator, "Optional", optionalEngine, required: false);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await Wait.UntilAsync(() => optionalEngine.State == EngineState.Ready, MustComplete, "the optional engine loads");

    var optional = Task.Run(() => coordinator.InvalidateContextForEngineAsync("Optional", ct), ct);
    await optionalAtReset.Task.WaitAsync(MustComplete, ct);
    var required = Task.Run(() => coordinator.InvalidateContextForEngineAsync("Required", ct), ct);
    await optional.WaitAsync(MustComplete, ct);
    optionalDone.Set();
    await required.WaitAsync(MustComplete, ct);
    await Wait.UntilAsync(() => Volatile.Read(ref loads) == 2, MustComplete, "the required engine reloads");
    var stateWhileReloading = coordinator.State;
    reloadGate.SetResult();

    stateWhileReloading.Should().NotBe(EngineCoordinatorState.Running, "the required engine is reloading");
    await Wait.UntilAsync(() => coordinator.State == EngineCoordinatorState.Running,
      MustComplete, "the coordinator runs again once the required engine is Ready");
  }
}
