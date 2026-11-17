using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for context operations (invalidations, refreshes and task resets) that overlap with each other or with
/// a generation switch, without the dispatcher: the last operation in flight decides when the coordinator is Running
/// again, and an operation that arrives while a switch rebuilds its engine acts on the next generation.
/// </summary>
public class ContextOperationTest
{
  /// <summary>
  /// An engine with one load task that runs <c>load</c> on every load.
  /// </summary>
  private sealed class LoadEngine(Func<Task> load) : BaseEngine(NullLogger.Instance)
  {
    private int _loads;

    public int Loads => Volatile.Read(ref _loads);

    protected override void SetUpTasks() => CreateTask("Load", async _ =>
    {
      Interlocked.Increment(ref _loads);
      await load().ConfigureAwait(false);
    });
  }

  /// <summary>
  /// The load of an engine: the first one completes at once, each later one waits for <see cref="Reload"/>.
  /// </summary>
  private sealed class GatedReload
  {
    private int _loads;

    public TaskCompletionSource Reload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Loads => Volatile.Read(ref _loads);

    public Task LoadAsync() => Interlocked.Increment(ref _loads) == 1 ? Task.CompletedTask : Reload.Task;
  }

  private static void Register(EngineCoordinator coordinator, string name, IEngine engine, bool required = true)
  {
    coordinator.RegisterEngine(name, _ => Task.FromResult(engine), requiredForRunning: required)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();
  }

  /// <summary>
  /// Verifies that the Ready event of an engine that reloads after one invalidation does not bring the coordinator back
  /// to Running while a second invalidation is in flight: the second one's engine reloads next, and the coordinator is
  /// Running only once that reload has ended.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_ReadyDuringAnotherInvalidation_RunningOnlyAfterTheLastReload()
  {
    var ct = TestContext.Current.CancellationToken;
    var a = new GatedReload();
    var b = new GatedReload();
    var bAtReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var aReloaded = new ManualResetEventSlim();
    // B's invalidation has begun (it counts as in flight) and waits, before it resets B, until A has reloaded.
    var logger = new OnMessageLogger("depending on and incl. B.", () =>
    {
      bAtReset.TrySetResult();
      aReloaded.Wait(MustComplete).Should().BeTrue("A reloads");
    });
    var coordinator = new EngineCoordinator(nameof(ContextOperationTest), logger, useDispatcher: false);
    var engineA = new LoadEngine(a.LoadAsync);
    var engineB = new LoadEngine(b.LoadAsync);
    Register(coordinator, "A", engineA);
    Register(coordinator, "B", engineB);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    await coordinator.InvalidateContextForEngineAsync("A", ct).WaitAsync(MustComplete, ct);
    await Wait.UntilAsync(() => engineA.Loads == 2, MustComplete, "A reloads");
    var invalidateB = Task.Run(() => coordinator.InvalidateContextForEngineAsync("B", ct), ct);
    await bAtReset.Task.WaitAsync(MustComplete, ct);
    a.Reload.SetResult();
    await Wait.UntilAsync(() => engineA.State == EngineState.Ready, MustComplete, "A is Ready again");
    aReloaded.Set();
    await invalidateB.WaitAsync(MustComplete, ct);
    await Wait.UntilAsync(() => engineB.Loads == 2, MustComplete, "B reloads");
    var stateWhileBReloads = coordinator.State;
    b.Reload.SetResult();

    stateWhileBReloads.Should().Be(EngineCoordinatorState.Initializing, "B is reloading");
    await Wait.UntilAsync(() => coordinator.State == EngineCoordinatorState.Running,
      MustComplete, "the coordinator runs again once B is Ready");
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that an operation that began while the coordinator was Initializing still brings it to Running once the
  /// required engines are Ready: here the required engine's Ready event arrives while an invalidation of an optional
  /// engine is in flight, so that invalidation's end brings the coordinator back.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_BeganWhileInitializing_RunningOnceRequiredReady()
  {
    var ct = TestContext.Current.CancellationToken;
    var required = new GatedReload();
    var optionalAtReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var requiredReloaded = new ManualResetEventSlim();
    var logger = new OnMessageLogger("depending on and incl. Optional.", () =>
    {
      optionalAtReset.TrySetResult();
      requiredReloaded.Wait(MustComplete).Should().BeTrue("the required engine reloads");
    });
    var coordinator = new EngineCoordinator(nameof(ContextOperationTest), logger, useDispatcher: false);
    var requiredEngine = new LoadEngine(required.LoadAsync);
    var optionalEngine = new LoadEngine(() => Task.CompletedTask);
    Register(coordinator, "Required", requiredEngine);
    Register(coordinator, "Optional", optionalEngine, required: false);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await Wait.UntilAsync(() => optionalEngine.State == EngineState.Ready, MustComplete, "the optional engine loads");

    await coordinator.InvalidateContextForEngineAsync("Required", ct).WaitAsync(MustComplete, ct);
    await Wait.UntilAsync(() => requiredEngine.Loads == 2, MustComplete, "the required engine reloads");
    coordinator.State.Should().Be(EngineCoordinatorState.Initializing);
    var invalidateOptional = Task.Run(() => coordinator.InvalidateContextForEngineAsync("Optional", ct), ct);
    await optionalAtReset.Task.WaitAsync(MustComplete, ct);
    required.Reload.SetResult();
    await Wait.UntilAsync(() => requiredEngine.State == EngineState.Ready, MustComplete, "the required engine is Ready");
    requiredReloaded.Set();
    await invalidateOptional.WaitAsync(MustComplete, ct);

    await Wait.UntilAsync(() => coordinator.State == EngineCoordinatorState.Running,
      MustComplete, "the coordinator runs once the required engine is Ready and no operation is in flight");
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// A generation switch held after its next generation has started, before it hands over: engine A is rebuilt from
  /// scratch and Ready in the next generation, while engine B, rebuilt too, still loads there.
  /// </summary>
  private sealed class HeldSwitch
  {
    private readonly List<LoadEngine> _instancesOfA = [];
    private readonly TaskCompletionSource _nextBLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _instancesOfB;

    public EngineCoordinator Coordinator { get; }

    public Task<EngineCoordinator> Switch { get; private set; }

    public HeldSwitch()
    {
      Coordinator = new EngineCoordinator(nameof(ContextOperationTest), NullLogger.Instance, useDispatcher: false);
      Coordinator.RegisterEngine("A", _ =>
        {
          var engine = new LoadEngine(() => Task.CompletedTask);
          lock (_instancesOfA)
            _instancesOfA.Add(engine);
          return Task.FromResult<IEngine>(engine);
        })
        .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
        .WithoutRestarts();
      Coordinator.RegisterEngine("B", _ => Task.FromResult<IEngine>(
          new LoadEngine(() => Interlocked.Increment(ref _instancesOfB) == 1 ? Task.CompletedTask : _nextBLoad.Task)))
        .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
        .WithoutRestarts();
    }

    public LoadEngine PreviousA => InstanceOfA(0);

    public LoadEngine NextA => InstanceOfA(1);

    private LoadEngine InstanceOfA(int index)
    {
      lock (_instancesOfA)
        return _instancesOfA.Count > index ? _instancesOfA[index] : null;
    }

    public async Task StartAndHoldAsync(CancellationToken ct)
    {
      await Coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
      Switch = Task.Run(() => Coordinator.RestartWithDefinitionsAsync(Coordinator.GetRegisteredDefinitions(),
        waitUntilReady: true, ct), ct);
      await Wait.UntilAsync(() => NextA?.State == EngineState.Ready, MustComplete,
        "the next generation's A is Ready");
      PreviousA.State.Should().Be(EngineState.Stopped, "the switch stopped the previous A before it started the next");
      Switch.IsCompleted.Should().BeFalse("the switch waits for the next generation's B");
    }

    public async Task<EngineCoordinator> ReleaseAsync(CancellationToken ct)
    {
      _nextBLoad.SetResult();
      return await Switch.WaitAsync(MustComplete, ct);
    }
  }

  /// <summary>
  /// Verifies that an invalidation that arrives while a switch rebuilds its engine, before the switch hands over, does
  /// not throw: it invalidates the next generation's engine, and the previous one stays stopped.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_DuringSwitchBeforeHandover_InvalidatesTheNextGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    var held = new HeldSwitch();
    await held.StartAndHoldAsync(ct);
    var previousLoads = held.PreviousA.Loads;

    await held.Coordinator.InvalidateContextForEngineAsync("A", ct).WaitAsync(MustComplete, ct);

    await Wait.UntilAsync(() => held.NextA.Loads == 2, MustComplete, "the next generation's A loads again");
    held.PreviousA.Loads.Should().Be(previousLoads, "the previous A is not started again");
    held.PreviousA.State.Should().Be(EngineState.Stopped);
    var next = await held.ReleaseAsync(ct);
    await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that a refresh that arrives while a switch rebuilds its engine, before the switch hands over, gives the
  /// context to the next generation's engine.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RefreshEngineContext_DuringSwitchBeforeHandover_RefreshesTheNextGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    var held = new HeldSwitch();
    await held.StartAndHoldAsync(ct);
    var context = new NoContext();

    await held.Coordinator.RefreshEngineContextAsync("A", context, ct).WaitAsync(MustComplete, ct);

    held.NextA.Context.Should().BeSameAs(context, "the next generation's A receives the context");
    held.PreviousA.Context.Should().NotBeSameAs(context);
    var next = await held.ReleaseAsync(ct);
    await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }
}
