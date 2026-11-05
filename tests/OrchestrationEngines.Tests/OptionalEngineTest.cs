using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
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
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// An engine with one load task that waits for a gate the test controls.
  /// </summary>
  private sealed class GatedEngine(Task loadGate) : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Load", async _ => await loadGate.ConfigureAwait(false));
  }

  private static EngineCoordinator CreateCoordinator() =>
    new(nameof(OptionalEngineTest), NullLogger.Instance, useDispatcher: false);

  private static void Register(EngineCoordinator coordinator, string name, IEngine engine, bool required)
  {
    coordinator.RegisterEngine(name, _ => Task.FromResult(engine), requiredForRunning: required)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));
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
    await WaitUntilAsync(() => coordinator.GetEngineIfReady("Optional") != null, "the optional engine finishes loading");
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
    await WaitUntilAsync(() => optional.State == EngineState.Faulted, "the optional engine fails");

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
    await WaitUntilAsync(() => optional.State == EngineState.Faulted, "the optional engine fails");
    coordinator.State.Should().Be(EngineCoordinatorState.Running);

    await coordinator.RefreshEngineContextAsync("Core", new NoContext(), ct).WaitAsync(MustComplete, ct);

    coordinator.State.Should().Be(EngineCoordinatorState.Running);
    core.State.Should().Be(EngineState.Ready);
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
    await WaitUntilAsync(() => optional.State == EngineState.Ready, "the optional engine loads at once");
    var completedEarly = await Task.WhenAny(ensureReady, Task.Delay(MustStayPending, ct)) == ensureReady;
    var stateWhileCoreLoads = coordinator.State;
    coreGate.SetResult();

    completedEarly.Should().BeFalse("the required engine was still loading");
    stateWhileCoreLoads.Should().NotBe(EngineCoordinatorState.Running);
    await ensureReady.WaitAsync(MustComplete, ct);
    coordinator.State.Should().Be(EngineCoordinatorState.Running);
  }
}
