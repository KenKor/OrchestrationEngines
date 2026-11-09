using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for letting go of the engine that a generation switch hands to the next generation's engine: the next
/// engine takes it over when it starts, and when it never starts (its base network or its creation failed), the previous
/// engine and all it loaded are not kept for as long as the next generation lives.
/// </summary>
public class PreviousEngineReleaseTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);
  private const string Base = "Base";
  private const string Patch = "Patch";

  /// <summary>
  /// An engine with one load task that holds on to the engine it replaces until it starts, as an engine
  /// does to take over data already loaded by its predecessor.
  /// </summary>
  private sealed class TakingOverEngine(bool loadFails = false) : BaseEngine(NullLogger.Instance), IReplacesPreviousEngine
  {
    private IEngine _previous;
    private int _releases;

    public IEngine Previous => Volatile.Read(ref _previous);

    public IEngine TookOver { get; private set; }

    public int Releases => Volatile.Read(ref _releases);

    public void Offer(IEngine previous) => Volatile.Write(ref _previous, previous);

    public void ReleasePreviousEngine()
    {
      Interlocked.Increment(ref _releases);
      Volatile.Write(ref _previous, null);
    }

    public override Task StartAsync()
    {
      TookOver = Interlocked.Exchange(ref _previous, null);
      return base.StartAsync();
    }

    protected override void SetUpTasks() =>
      CreateTask("Load", _ => loadFails ? throw new InvalidOperationException("The load fails.") : Task.CompletedTask);
  }

  private static EngineCoordinator CreateCoordinator() =>
    new(nameof(PreviousEngineReleaseTest), NullLogger.Instance, useDispatcher: false);

  /// <summary>
  /// A descriptor whose first engine loads, and whose engine in a switch comes from <paramref name="next"/>, offered the
  /// engine it replaces.
  /// </summary>
  private static EngineDescriptor Descriptor(string name, bool required, Func<IEngine, IEngine> next, string builtOn = null)
  {
    var descriptor = new EngineDescriptor(name, _ => Task.FromResult<IEngine>(new TakingOverEngine()), required, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .SetRestartFactory((input, _) => Task.FromResult(next(input.PreviousEngine)))
      // A failed load or creation must fail at once, not retry by the default restart policy.
      .WithoutRestarts();
    return builtOn == null ? descriptor : descriptor.AddDependsOn(builtOn);
  }

  private static IEngine Offered(TakingOverEngine engine, IEngine previous)
  {
    engine.Offer(previous);
    return engine;
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
  /// Verifies that an engine that is not required for running (for example, an engine used only for editing) still takes over the engine it
  /// replaces: it is let go of only once the engine has started, not when the next generation is ready (which does not
  /// wait for an engine that is not required).
  /// </summary>
  [Fact]
  public async Task RestartWithDefinitions_NextEngineStarts_TakesOverThePreviousEngine()
  {
    var ct = TestContext.Current.CancellationToken;
    var next = new TakingOverEngine();
    var coordinator = CreateCoordinator();
    coordinator.RegisterEngineDescriptor(Descriptor(Patch, required: false, previous => Offered(next, previous)));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => coordinator.GetEngineIfReady(Patch) != null, "the first engine must load");
    var previous = coordinator.GetEngineIfReady(Patch);

    var nextCoordinator = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: false, ct)
      .WaitAsync(MustComplete, ct);
    await WaitUntilAsync(() => nextCoordinator.GetEngineIfReady(Patch) != null, "the next engine must load");

    next.TookOver.Should().BeSameAs(previous);
    await WaitUntilAsync(() => next.Releases == 1, "the next engine must be told to let go once it has started");
    next.Previous.Should().BeNull();
  }

  /// <summary>
  /// Verifies that a data patch whose next engine never starts, because the network it is built on fails in the next
  /// generation, lets go of the engine it replaces.
  /// </summary>
  [Fact]
  public async Task RestartWithDefinitions_NextEngineNeverStarts_LetsGoOfThePreviousEngine()
  {
    var ct = TestContext.Current.CancellationToken;
    var nextPatch = new TakingOverEngine();
    var coordinator = CreateCoordinator();
    coordinator.RegisterEngineDescriptor(Descriptor(Base, required: true, _ => new TakingOverEngine(loadFails: true)));
    coordinator.RegisterEngineDescriptor(Descriptor(Patch, required: true, previous => Offered(nextPatch, previous), builtOn: Base));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var nextCoordinator = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: false, ct)
      .WaitAsync(MustComplete, ct);

    await WaitUntilAsync(() => nextPatch.Releases == 1, "the patch engine that never starts must let go of the previous engine");
    nextPatch.Previous.Should().BeNull();
    nextPatch.TookOver.Should().BeNull("it never started");
    nextCoordinator.GetEngineIfReady(Patch).Should().BeNull();
  }

  /// <summary>
  /// Verifies that the previous engine can be collected when the next engine's creation fails: the switch's handover of
  /// the previous engine was only released after a successful creation.
  /// </summary>
  [Fact]
  public async Task RestartWithDefinitions_NextEngineCreationFails_ThePreviousEngineCanBeCollected()
  {
    var (previous, nextCoordinator) = await SwitchWithFailingCreationAsync();
    await WaitUntilAsync(() => nextCoordinator.GetEngineIfCreated(Patch) == null && nextCoordinator.State != EngineCoordinatorState.Created,
      "the next generation must have started");

    // Work of the shutdown may still be finishing on the thread pool for a moment: collect a few times, briefly apart. A
    // real reference keeps the engine alive however long this waits.
    for (var i = 0; i < 10 && previous.IsAlive; i++)
    {
      await Task.Delay(100, TestContext.Current.CancellationToken);
      GC.Collect();
      GC.WaitForPendingFinalizers();
      GC.Collect();
    }

    previous.IsAlive.Should().BeFalse("nothing may keep the previous engine alive once its replacement failed");
    GC.KeepAlive(nextCoordinator);
  }

  /// <summary>
  /// Loads a first generation and switches to a next one whose engine cannot be created. Not inlined, so that no local of
  /// the test keeps the previous engine or its coordinator alive.
  /// </summary>
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static async Task<(WeakReference previous, EngineCoordinator nextCoordinator)> SwitchWithFailingCreationAsync()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    coordinator.RegisterEngineDescriptor(Descriptor(Patch, required: true, _ => throw new InvalidOperationException("The creation fails.")));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var previous = new WeakReference(coordinator.GetEngineIfReady(Patch));
    var nextCoordinator = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct)
      .WaitAsync(MustComplete, ct);
    return (previous, nextCoordinator);
  }
}
