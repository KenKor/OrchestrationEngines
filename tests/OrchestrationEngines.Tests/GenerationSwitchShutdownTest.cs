using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Restarter;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for what a generation switch (RestartWithDefinitionsAsync) does with the generation it replaces: it
/// shuts it down completely (also its Faulted engines and their recovery), answers lookups that wait on it from the
/// next generation, and does not keep its engines alive.
/// </summary>
public class GenerationSwitchShutdownTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);
  private const string Name = "N";

  /// <summary>
  /// A network engine with one load task, whose work the test decides; counts its loads.
  /// </summary>
  private sealed class NetworkEngine(Func<int, CancellationToken, Task> loadAsync) : BaseEngine(NullLogger.Instance)
  {
    private int _loads;

    public int Loads => Volatile.Read(ref _loads);

    protected override void SetUpTasks() => CreateTask("Load", token => loadAsync(Interlocked.Increment(ref _loads), token));
  }

  private static NetworkEngine Quick() => new((_, _) => Task.CompletedTask);

  private static EngineCoordinator CreateCoordinator() =>
    new(nameof(GenerationSwitchShutdownTest), NullLogger.Instance, useDispatcher: false);

  /// <summary>
  /// A descriptor whose first engine comes from <paramref name="first"/> and every later one (a generation switch, which
  /// offers the previous engine) is a quick engine.
  /// </summary>
  private static EngineDescriptor Descriptor(string name, Func<IEngine> first, RestartPolicy recovery = null)
  {
    var descriptor = new EngineDescriptor(name, _ => Task.FromResult(first()), requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .SetRestartFactory((_, _) => Task.FromResult<IEngine>(Quick()));
    return recovery == null ? descriptor : descriptor.SetRecoveryHelper(recovery);
  }

  /// <summary>
  /// An engine that loads at once and throws once it has stopped.
  /// </summary>
  private sealed class StopFailingEngine(Exception failure) : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Load", _ => Task.CompletedTask);

    public override async Task StopAsync()
    {
      await base.StopAsync().ConfigureAwait(false);
      throw failure;
    }
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
  /// Verifies that a switch stops an engine that is Faulted and whose kick-start is waiting to retry, and ends that
  /// retry: the replaced generation must not load the network again next to the new one.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RestartWithDefinitions_FaultedEngineWithRecovery_IsStoppedAndNotLoadedAgain()
  {
    var ct = TestContext.Current.CancellationToken;
    var failing = new NetworkEngine((_, _) => throw new InvalidOperationException("The load failed."));
    var coordinator = CreateCoordinator();
    var slowRetry = new RestartPolicy
    {
      MaxRestartsInWindow = 5,
      Window = TimeSpan.FromMinutes(10),
      InitialBackoff = TimeSpan.FromSeconds(1),
      MaxBackoff = TimeSpan.FromSeconds(1),
    };
    coordinator.RegisterEngineDescriptor(Descriptor(Name, () => failing, slowRetry));
    _ = coordinator.StartAsync();
    await WaitUntilAsync(() => failing.State == EngineState.Faulted, "the first load must fail");

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: false, ct)
      .WaitAsync(MustComplete, ct);

    failing.IsCancellationRequested.Should().BeTrue("the switch stops every engine of the replaced generation");
    coordinator.State.Should().Be(EngineCoordinatorState.Stopped);
    var loadsAtSwitch = failing.Loads;
    await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
    failing.Loads.Should().Be(loadsAtSwitch, "the replaced generation's kick-start must not retry the load");
    (await next.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct)).Should().NotBeNull().And.NotBeSameAs(failing);
  }

  /// <summary>
  /// Verifies that a switch whose replaced engine fails to stop, after the next generation has taken over, still
  /// returns that next generation: the replaced coordinator ends <see cref="EngineCoordinatorState.Stopped"/>, and the
  /// failure is logged as one from the replaced generation.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RestartWithDefinitions_ReplacedEngineThrowsOnStop_ReturnsTheNextGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    var replaced = new StopFailingEngine(new InvalidOperationException("The stop failed."));
    var logged = 0;
    var logger = new OnMessageLogger("generation this switch replaced", () => Interlocked.Increment(ref logged));
    var coordinator = new EngineCoordinator(nameof(GenerationSwitchShutdownTest), logger, useDispatcher: false);
    coordinator.RegisterEngineDescriptor(Descriptor(Name, () => replaced));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct)
      .WaitAsync(MustComplete, ct);

    next.Should().NotBeNull().And.BeSameAs(coordinator.Successor);
    next.Engines[Name].Should().NotBeSameAs(replaced);
    coordinator.State.Should().Be(EngineCoordinatorState.Stopped);
    Volatile.Read(ref logged).Should().Be(1);
  }

  /// <summary>
  /// Verifies that a lookup waiting on the replaced generation is answered at once by the next generation's engine,
  /// instead of failing after the replaced generation's kick-start gave up.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RestartWithDefinitions_PendingLookup_IsAnsweredByTheNextGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    var loading = new NetworkEngine((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
    var coordinator = CreateCoordinator();
    coordinator.RegisterEngineDescriptor(Descriptor(Name, () => loading, RestartPolicy.Default));
    var lookup = coordinator.GetEngineAsync(Name, ct);
    await WaitUntilAsync(() => loading.Loads == 1, "the first generation must be loading");

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: false, ct)
      .WaitAsync(MustComplete, ct);
    var engine = await lookup.WaitAsync(TimeSpan.FromSeconds(3), ct);

    engine.Should().NotBeNull().And.NotBeSameAs(loading);
    engine.Should().BeSameAs(next.Engines[Name]);
    engine.State.Should().Be(EngineState.Ready);
  }

  /// <summary>
  /// Verifies that a lookup made on the replaced coordinator after the switch (a caller that still holds it) is answered
  /// by the next generation too, also the one that does not wait.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_ReplacedCoordinator_LaterLookups_AreAnsweredByTheNextGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    coordinator.RegisterEngineDescriptor(Descriptor(Name, Quick));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct)
      .WaitAsync(MustComplete, ct);

    coordinator.Successor.Should().BeSameAs(next);
    (await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct)).Should().BeSameAs(next.Engines[Name]);
    (await coordinator.GetEngineAsync(Name, 1000, ct).WaitAsync(MustComplete, ct)).Should().BeSameAs(next.Engines[Name]);
    coordinator.GetEngineIfReady(Name).Should().BeSameAs(next.Engines[Name]);
  }

  /// <summary>
  /// Verifies that while a switch shuts the replaced generation down, the replaced coordinator's current coordinator is
  /// already the next one: a reader such as the states endpoint then reports the next generation, not the one being
  /// stopped.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RestartWithDefinitions_WhileShuttingDown_CurrentIsTheNextGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    var previous = Quick();
    var coordinator = CreateCoordinator();
    coordinator.RegisterEngineDescriptor(Descriptor(Name, () => previous));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    EngineCoordinator currentWhileStopping = null;
    previous.EngineStatusChanged += (_, e) =>
    {
      if (e.State == EngineState.Stopped)
        currentWhileStopping = coordinator.Current;
    };

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct)
      .WaitAsync(MustComplete, ct);

    currentWhileStopping.Should().BeSameAs(next, "the switch hands over before it stops the replaced generation");
    coordinator.Current.Should().BeSameAs(next);
    next.Current.Should().BeSameAs(next);
  }

  /// <summary>
  /// Verifies that a lookup waiting for an engine that the switch releases (leaves out) ends promptly with null.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RestartWithDefinitions_PendingLookupForReleasedEngine_ReturnsNull()
  {
    var ct = TestContext.Current.CancellationToken;
    var loading = new NetworkEngine((_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
    var coordinator = CreateCoordinator();
    coordinator.RegisterEngineDescriptor(Descriptor(Name, () => loading, RestartPolicy.Default));
    coordinator.RegisterEngineDescriptor(Descriptor("Other", Quick));
    var lookup = coordinator.GetEngineAsync(Name, ct);
    await WaitUntilAsync(() => loading.Loads == 1, "the first generation must be loading");

    var remaining = coordinator.GetRegisteredDefinitions().Where(d => d.Name != Name);
    await coordinator.RestartWithDefinitionsAsync(remaining, waitUntilReady: false, ct).WaitAsync(MustComplete, ct);

    (await lookup.WaitAsync(TimeSpan.FromSeconds(3), ct)).Should().BeNull();
  }

  /// <summary>
  /// Verifies that the engines of replaced generations can be collected: a switch hands the previous engine to the next
  /// generation once, and a later switch starts from the registered factory instead of a chain of handovers.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RestartWithDefinitions_ReplacedGenerations_CanBeCollected()
  {
    var (replacedEngines, current) = await SwitchSeveralTimesAsync(generations: 5);

    // Work of the last shutdown may still be finishing on the thread pool and hold its generation for a moment: collect
    // a few times, briefly apart. A real reference keeps an engine alive however long this waits.
    for (var i = 0; i < 10 && replacedEngines.Any(e => e.IsAlive); i++)
    {
      await Task.Delay(100, TestContext.Current.CancellationToken);
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
    }

    replacedEngines.Where(e => e.IsAlive).Should().BeEmpty("nothing may keep a replaced generation's engine alive");
    current.GetEngineIfReady(Name).Should().NotBeNull();
  }

  /// <summary>
  /// Starts a coordinator and switches generations as a host does (passing the registered definitions back), and
  /// returns weak references to the engines of the replaced generations, and the current coordinator. Not inlined, so
  /// that no local of the test keeps a replaced engine alive.
  /// </summary>
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static async Task<(List<WeakReference> replacedEngines, EngineCoordinator current)> SwitchSeveralTimesAsync(int generations)
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    coordinator.RegisterEngineDescriptor(Descriptor(Name, Quick));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var replacedEngines = new List<WeakReference>();
    for (var i = 0; i < generations; i++)
    {
      replacedEngines.Add(new WeakReference(coordinator.Engines[Name]));
      coordinator = Switch(coordinator);
    }
    return (replacedEngines, coordinator);
  }

  /// <summary>
  /// Switches to the next generation the way a host typically does: synchronously, without keeping the switch's task (which
  /// refers to the replaced coordinator for as long as it is held).
  /// </summary>
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static EngineCoordinator Switch(EngineCoordinator coordinator) =>
    coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, TestContext.Current.CancellationToken)
      .WaitAsync(MustComplete, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
}
