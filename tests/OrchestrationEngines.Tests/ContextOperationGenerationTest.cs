using System;
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
/// Contains tests for context operations (an invalidation or a refresh) on a coordinator that a generation switch
/// replaced, which hand off to the newer generation (#86, item 1), and for overlapping context operations without the
/// dispatcher, which must not bring the coordinator back to Running while a required engine reloads (#86, item 4).
/// </summary>
public class ContextOperationGenerationTest
{
  private const string Name = "N";

  /// <summary>
  /// A context that carries the number of the context factory call that made it.
  /// </summary>
  private sealed class NumberedContext(int number) : BaseEngineContext
  {
    public int Number { get; } = number;

    public override bool Equals(IEngineContext other) => other is NumberedContext o && o.Number == Number;

    public override bool Equals(object obj) => obj is IEngineContext ctx && Equals(ctx);

    public override int GetHashCode() => Number;
  }

  /// <summary>
  /// The context the factory made for <paramref name="engine"/>, under any context the coordinator wraps it in.
  /// </summary>
  private static IEngineContext Own(IEngine engine) => engine.Context.Inner ?? engine.Context;

  /// <summary>
  /// An engine with one load task, whose work the test decides.
  /// </summary>
  private sealed class LoadEngine(Func<CancellationToken, Task> loadAsync) : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Load", loadAsync);
  }

  private static LoadEngine Quick() => new(_ => Task.CompletedTask);

  /// <summary>
  /// A required engine that loads at once, whose context factory numbers each context it makes, and whose generation
  /// switch makes a new engine.
  /// </summary>
  private static EngineDescriptor Descriptor(string name, Func<int> nextNumber) =>
    new EngineDescriptor(name, _ => Task.FromResult<IEngine>(Quick()), requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NumberedContext(nextNumber())))
      .SetRestartFactory((_, _) => Task.FromResult<IEngine>(Quick()));

  /// <summary>
  /// A started coordinator with the engines <paramref name="names"/>, replaced by a generation switch that keeps only
  /// the engine <see cref="Name"/>.
  /// </summary>
  private static async Task<(EngineCoordinator Replaced, EngineCoordinator Next)> SwitchedAsync(
    bool useDispatcher, Func<int> nextNumber, CancellationToken ct, params string[] names)
  {
    var coordinator = new EngineCoordinator(nameof(ContextOperationGenerationTest), NullLogger.Instance, useDispatcher);
    foreach (var name in names)
      coordinator.RegisterEngineDescriptor(Descriptor(name, nextNumber));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var kept = coordinator.GetRegisteredDefinitions().Where(d => d.Name == Name).ToList();
    var next = await coordinator.RestartWithDefinitionsAsync(kept, waitUntilReady: true, ct)
      .WaitAsync(MustComplete, ct);
    next.Should().NotBeSameAs(coordinator);
    coordinator.Successor.Should().BeSameAs(next);
    return (coordinator, next);
  }

  /// <summary>
  /// Verifies that invalidating a context on a replaced coordinator invalidates it on the newer generation: its engine
  /// gets a context from a new factory call.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task EngineCoordinator_InvalidateContext_ReplacedCoordinator_InvalidatesOnTheNextGeneration(
    bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    var calls = 0;
    var (replaced, next) = await SwitchedAsync(useDispatcher, () => Interlocked.Increment(ref calls), ct, Name);
    var engine = next.Engines[Name];
    var before = ((NumberedContext)Own(engine)).Number;

    await replaced.InvalidateContextForEngineAsync(Name, ct).WaitAsync(MustComplete, ct);
    await next.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    ((NumberedContext)Own(next.Engines[Name])).Number.Should().BeGreaterThan(before, "the context was made again");
    next.State.Should().Be(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Verifies that refreshing a context on a replaced coordinator pushes it into the newer generation's engine.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task EngineCoordinator_RefreshEngineContext_ReplacedCoordinator_PushesIntoTheNextGeneration(
    bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    var calls = 0;
    var (replaced, next) = await SwitchedAsync(useDispatcher, () => Interlocked.Increment(ref calls), ct, Name);
    var fresh = new NumberedContext(-1);

    await replaced.RefreshEngineContextAsync(Name, fresh, ct).WaitAsync(MustComplete, ct);

    Own(next.Engines[Name]).Should().BeSameAs(fresh);
    next.State.Should().Be(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Verifies that a context operation on a replaced coordinator, for an engine the newer generation does not have,
  /// fails as for an unknown name.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task EngineCoordinator_ContextOperations_ReplacedCoordinatorEngineDropped_ThrowArgumentException(
    bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    var calls = 0;
    var (replaced, _) = await SwitchedAsync(useDispatcher, () => Interlocked.Increment(ref calls), ct, Name, "Dropped");

    var invalidate = () => replaced.InvalidateContextForEngineAsync("Dropped", ct).WaitAsync(MustComplete, ct);
    var refresh = () => replaced.RefreshEngineContextAsync("Dropped", new NoContext(), ct).WaitAsync(MustComplete, ct);

    await invalidate.Should().ThrowAsync<ArgumentException>();
    await refresh.Should().ThrowAsync<ArgumentException>();
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
    var coordinator = new EngineCoordinator(nameof(ContextOperationGenerationTest), logger, useDispatcher: false);
    var requiredEngine = new LoadEngine(_ => Interlocked.Increment(ref loads) == 1 ? Task.CompletedTask : reloadGate.Task);
    coordinator.RegisterEngine("Required", _ => Task.FromResult<IEngine>(requiredEngine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();
    coordinator.RegisterEngine("Optional", _ => Task.FromResult<IEngine>(Quick()), requiredForRunning: false)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    await Wait.UntilAsync(() => coordinator.GetEngineIfReady("Optional") != null, MustComplete, "both engines load");

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
