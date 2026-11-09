using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for data patches: networks built on an underlying network (for example a planned version on top of
/// the as-built network). The engine of a data patch depends on the engine of the network it is built on.
/// </summary>
public class DataPatchTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// A network engine with one load task, counting its loads; the test decides what a load does.
  /// </summary>
  private sealed class NetworkEngine(Func<int, CancellationToken, Task> loadAsync = null) : BaseEngine(NullLogger.Instance)
  {
    private int _loads;

    public int Loads => Volatile.Read(ref _loads);

    protected override void SetUpTasks() =>
      CreateTask("Load", token => (loadAsync ?? ((_, _) => Task.CompletedTask))(Interlocked.Increment(ref _loads), token));
  }

  /// <summary>
  /// Counts how often each engine's context was computed, which happens again when its context is invalidated.
  /// </summary>
  private sealed class ContextLog
  {
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    public int this[string name] => _counts.GetValueOrDefault(name);

    public Task<IEngineContext> Compute(string name)
    {
      _counts.AddOrUpdate(name, 1, (_, count) => count + 1);
      return Task.FromResult<IEngineContext>(new NoContext());
    }
  }

  private static EngineCoordinator CreateCoordinator() =>
    new(nameof(DataPatchTest), NullLogger.Instance, useDispatcher: false);

  private static EngineDescriptor Register(EngineCoordinator coordinator, string name, IEngine engine, string builtOn = null,
    ContextLog contexts = null)
  {
    var descriptor = coordinator.RegisterEngine(name, _ => Task.FromResult(engine), requiredForRunning: true)
      .SetContextFactory(_ => contexts?.Compute(name) ?? Task.FromResult<IEngineContext>(new NoContext()))
      // A failed load must fault at once, not retry by the default restart policy.
      .WithoutRestarts();
    if (builtOn != null)
      descriptor.AddDependsOn(builtOn);
    return descriptor;
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
  /// Verifies that looking up a data patch whose underlying network failed to load returns null instead of waiting
  /// forever.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_GetEngineAsync_DataPatchOnFailedNetwork_ReturnsNullWithoutHanging()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    Register(coordinator, "AsBuilt", new NetworkEngine((_, _) => throw new InvalidOperationException("The load failed.")));
    Register(coordinator, "Planned", new NetworkEngine(), builtOn: "AsBuilt");

    var patch = await coordinator.GetEngineAsync("Planned", ct).WaitAsync(MustComplete, ct);

    patch.Should().BeNull();
  }

  /// <summary>
  /// Verifies that invalidating the context of the as-built network computes the contexts of it and its data patch
  /// again, and loads both again.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_Base_ReloadsItsDataPatches()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var contexts = new ContextLog();
    var asBuilt = new NetworkEngine();
    var planned = new NetworkEngine();
    Register(coordinator, "AsBuilt", asBuilt, contexts: contexts);
    Register(coordinator, "Planned", planned, builtOn: "AsBuilt", contexts: contexts);
    await coordinator.GetEngineAsync("Planned", ct).WaitAsync(MustComplete, ct);

    await coordinator.InvalidateContextForEngineAsync("AsBuilt", ct).WaitAsync(MustComplete, ct);
    var patch = await coordinator.GetEngineAsync("Planned", ct).WaitAsync(MustComplete, ct);

    patch.Should().BeSameAs(planned);
    contexts["AsBuilt"].Should().Be(2);
    contexts["Planned"].Should().Be(2);
    asBuilt.Loads.Should().Be(2);
    planned.Loads.Should().Be(2);
  }

  /// <summary>
  /// Verifies that invalidating the context of a data patch loads only the patch again, not the network it is built
  /// on.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_InvalidateContext_DataPatch_LeavesBaseLoaded()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var contexts = new ContextLog();
    var asBuilt = new NetworkEngine();
    var planned = new NetworkEngine();
    Register(coordinator, "AsBuilt", asBuilt, contexts: contexts);
    Register(coordinator, "Planned", planned, builtOn: "AsBuilt", contexts: contexts);
    await coordinator.GetEngineAsync("Planned", ct).WaitAsync(MustComplete, ct);

    await coordinator.InvalidateContextForEngineAsync("Planned", ct).WaitAsync(MustComplete, ct);
    var patch = await coordinator.GetEngineAsync("Planned", ct).WaitAsync(MustComplete, ct);

    patch.Should().BeSameAs(planned);
    contexts["AsBuilt"].Should().Be(1);
    contexts["Planned"].Should().Be(2);
    asBuilt.Loads.Should().Be(1);
    planned.Loads.Should().Be(2);
  }

  /// <summary>
  /// Verifies that the as-built network loading again on its own (its tasks reset, as a change monitor does) leaves
  /// its data patch loaded and Ready: reloading data patches is a separate mechanism, not a cascade of the engine
  /// framework.
  /// </summary>
  [Fact]
  public async Task BaseEngine_TriggerResetAsync_Base_LeavesDataPatchLoaded()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var asBuilt = new NetworkEngine();
    var planned = new NetworkEngine();
    Register(coordinator, "AsBuilt", asBuilt);
    Register(coordinator, "Planned", planned, builtOn: "AsBuilt");
    await coordinator.GetEngineAsync("Planned", ct).WaitAsync(MustComplete, ct);

    await asBuilt.TriggerResetAsync().WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => asBuilt.State == EngineState.Ready, "the as-built network must load again");

    asBuilt.Loads.Should().Be(2);
    planned.Loads.Should().Be(1);
    coordinator.GetEngineIfReady("Planned").Should().BeSameAs(planned);
  }

  /// <summary>
  /// Verifies that a generation switch that does not wait until ready (as a host may do) still offers each restart
  /// factory its previous engine with nothing changed, although the previous generation is stopped straight away,
  /// and that the new generation becomes ready.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RestartWithDefinitions_WithoutWaiting_StillOffersPreviousEngine()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var offered = new ConcurrentDictionary<string, EngineRestartInput>(StringComparer.OrdinalIgnoreCase);
    foreach (var (name, builtOn) in new[] { ("AsBuilt", (string)null), ("Planned", "AsBuilt") })
    {
      Register(coordinator, name, new NetworkEngine(), builtOn)
        .SetRestartFactory((input, _) =>
        {
          offered[input.Name] = input;
          return Task.FromResult<IEngine>(new NetworkEngine());
        });
    }
    await coordinator.GetEngineAsync("Planned", ct).WaitAsync(MustComplete, ct);
    var previousPatch = coordinator.Engines["Planned"];

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: false, ct)
      .WaitAsync(MustComplete, ct);
    var patch = await next.GetEngineAsync("Planned", ct).WaitAsync(MustComplete, ct);

    patch.Should().NotBeNull().And.NotBeSameAs(previousPatch);
    offered["Planned"].PreviousEngine.Should().BeSameAs(previousPatch);
    offered["Planned"].NothingChanged.Should().BeTrue();
    offered["AsBuilt"].NothingChanged.Should().BeTrue();
  }

  /// <summary>
  /// Verifies the engine side of a data patch reload triggered from outside, as it arrives on a server host: a
  /// generation switch with a version bump of the patch. The patch loads again (its definition changed), the patch
  /// built on it loads again (its dependency changed), and the as-built network is offered its previous engine with
  /// nothing changed, so it can keep its loaded state.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RestartWithDefinitions_DataPatchVersionBump_ReloadsPatchesButNotBase()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var offered = new ConcurrentDictionary<string, EngineRestartInput>(StringComparer.OrdinalIgnoreCase);
    EngineDescriptor Describe(string name, string builtOn, int version)
    {
      var descriptor = new EngineDescriptor(name, _ => Task.FromResult<IEngine>(new NetworkEngine()), requiredForRunning: true, version)
        .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
        .SetRestartFactory((input, _) =>
        {
          offered[input.Name] = input;
          return Task.FromResult<IEngine>(new NetworkEngine());
        });
      if (builtOn != null)
        descriptor.AddDependsOn(builtOn);
      return descriptor;
    }
    var stack = new[] { ("AsBuilt", (string)null), ("Planned", "AsBuilt"), ("PlannedOnPlanned", "Planned") };
    foreach (var (name, builtOn) in stack)
      coordinator.RegisterEngineDescriptor(Describe(name, builtOn, version: 1));
    await coordinator.GetEngineAsync("PlannedOnPlanned", ct).WaitAsync(MustComplete, ct);
    var candidates = new[]
    {
      Describe("AsBuilt", null, version: 1),
      Describe("Planned", "AsBuilt", version: 2),
      Describe("PlannedOnPlanned", "Planned", version: 1),
    };

    var next = await coordinator.RestartWithDefinitionsAsync(candidates, waitUntilReady: true, ct).WaitAsync(MustComplete, ct);

    offered["AsBuilt"].NothingChanged.Should().BeTrue();
    offered["Planned"].DefinitionChanged.Should().BeTrue();
    offered["PlannedOnPlanned"].DependencyChanged.Should().BeTrue();
    next.GetEngineIfReady("PlannedOnPlanned").Should().NotBeNull();
  }

  /// <summary>
  /// Verifies the engine side of a data patch reload triggered from outside, as it arrives from an editing client: a fresh
  /// context for the patch only. The patch gets the new context and decides itself what to load again; the as-built
  /// network keeps its context.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RefreshEngineContextAsync_DataPatch_LeavesBaseContext()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var asBuilt = new NetworkEngine();
    var planned = new NetworkEngine();
    Register(coordinator, "AsBuilt", asBuilt);
    Register(coordinator, "Planned", planned, builtOn: "AsBuilt");
    await coordinator.GetEngineAsync("Planned", ct).WaitAsync(MustComplete, ct);
    var baseContext = asBuilt.Context;
    var freshContext = new NoContext();

    await coordinator.RefreshEngineContextAsync("Planned", freshContext, ct).WaitAsync(MustComplete, ct);

    planned.Context.Inner.Should().BeSameAs(freshContext, "the patch's context wraps the fresh one with the network it is built on");
    asBuilt.Context.Should().BeSameAs(baseContext);
    asBuilt.Loads.Should().Be(1);
    coordinator.GetEngineIfReady("Planned").Should().BeSameAs(planned);
  }

  /// <summary>
  /// Verifies that a data patch registered before the network it is built on loads, as long as both are registered
  /// before the coordinator starts: the dependency is only looked up at start.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_StartAsync_DataPatchRegisteredBeforeItsBase_Loads()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var planned = new NetworkEngine();
    Register(coordinator, "Planned", planned, builtOn: "AsBuilt");
    Register(coordinator, "AsBuilt", new NetworkEngine());

    var patch = await coordinator.GetEngineAsync("Planned", ct).WaitAsync(MustComplete, ct);

    patch.Should().BeSameAs(planned);
  }

  /// <summary>
  /// Verifies that starting a coordinator with a data patch whose underlying network was never registered fails.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_StartAsync_DataPatchOfUnregisteredNetwork_Throws()
  {
    var coordinator = CreateCoordinator();
    Register(coordinator, "Planned", new NetworkEngine(), builtOn: "AsBuilt");

    var start = () => coordinator.StartAsync();

    await start.Should().ThrowAsync<KeyNotFoundException>();
  }

  /// <summary>
  /// Verifies that starting a coordinator with two data patches built on each other fails on the cycle.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_StartAsync_DataPatchesBuiltOnEachOther_Throws()
  {
    var coordinator = CreateCoordinator();
    Register(coordinator, "Planned", new NetworkEngine(), builtOn: "Repair");
    Register(coordinator, "Repair", new NetworkEngine(), builtOn: "Planned");

    var start = () => coordinator.StartAsync();

    await start.Should().ThrowAsync<CyclicEngineTaskDependencyException>();
  }

  /// <summary>
  /// Verifies that waiting on an engine whose kick-start was reset (a context invalidation) does not wait forever when
  /// the kick-start does not run again, because computing the new context failed. The reset prepared a restart of the
  /// engine that nobody carries out; the engine is given up on instead, as a data patch that waits for the network it is
  /// built on would otherwise hang.
  /// </summary>
  [Fact]
  public async Task BaseEngine_EnsureReadyAsync_KickStartResetButContextFails_DoesNotWaitForever()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var asBuilt = new NetworkEngine();
    var contexts = 0;
    coordinator.RegisterEngine("AsBuilt", _ => Task.FromResult<IEngine>(asBuilt), requiredForRunning: true)
      .SetContextFactory(_ => Interlocked.Increment(ref contexts) == 1
        ? Task.FromResult<IEngineContext>(new NoContext())
        : throw new InvalidOperationException("The context could not be computed."))
      // The failed context must fail at once, not retry by the default restart policy.
      .WithoutRestarts();
    await coordinator.GetEngineAsync("AsBuilt", ct).WaitAsync(MustComplete, ct);

    await coordinator.InvalidateContextForEngineAsync("AsBuilt", ct).WaitAsync(MustComplete, ct);
    var lookup = await coordinator.GetEngineAsync("AsBuilt", ct).WaitAsync(MustComplete, ct);
    var ready = await Record.ExceptionAsync(() => asBuilt.EnsureReadyAsync().WaitAsync(MustComplete, ct));

    contexts.Should().Be(2);
    lookup.Should().BeNull();
    ready.Should().BeNull("the wait ends instead of timing out");
    asBuilt.State.Should().Be(EngineState.Faulted);
  }

  /// <summary>
  /// Verifies that a failed start is final for the coordinator: a later start, and a later wait for readiness, throw the
  /// same exception instead of starting again or starting the task graph that failed.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_StartAsync_AfterAFailedStart_ThrowsTheSameException()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    Register(coordinator, "Planned", new NetworkEngine(), builtOn: "AsBuilt");

    var first = await Record.ExceptionAsync(() => coordinator.StartAsync());
    var second = await Record.ExceptionAsync(() => coordinator.StartAsync());
    var ready = await Record.ExceptionAsync(() => coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct));

    first.Should().BeOfType<KeyNotFoundException>();
    second.Should().BeSameAs(first);
    ready.Should().BeSameAs(first);
  }

  /// <summary>
  /// Verifies that a coordinator whose start failed recovers through a generation switch with definitions that are
  /// complete: the next generation becomes ready, and the failed one is shut down.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RestartWithDefinitionsAsync_AfterAFailedStart_NextGenerationBecomesReady()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var planned = new NetworkEngine();
    Register(coordinator, "Planned", planned, builtOn: "AsBuilt");
    var start = () => coordinator.StartAsync();
    await start.Should().ThrowAsync<KeyNotFoundException>();

    var asBuilt = new NetworkEngine();
    var asBuiltDescriptor = new EngineDescriptor("AsBuilt", _ => Task.FromResult<IEngine>(asBuilt), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));
    var next = await coordinator
      .RestartWithDefinitionsAsync([.. coordinator.GetRegisteredDefinitions(), asBuiltDescriptor], token: ct)
      .WaitAsync(MustComplete, ct);
    var patch = await next.GetEngineAsync("Planned", ct).WaitAsync(MustComplete, ct);

    patch.Should().BeSameAs(planned);
    asBuilt.Loads.Should().Be(1);
    coordinator.State.Should().Be(EngineCoordinatorState.Stopped);
  }
}
