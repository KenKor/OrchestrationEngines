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
/// replaced, which hand off to the newer generation (#86, item 1).
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
  /// An engine with one load task that ends at once.
  /// </summary>
  private sealed class QuickEngine() : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Load", _ => Task.CompletedTask);
  }

  /// <summary>
  /// A required engine that loads at once, whose context factory numbers each context it makes, and whose generation
  /// switch makes a new engine.
  /// </summary>
  private static EngineDescriptor Descriptor(string name, Func<int> nextNumber) =>
    new EngineDescriptor(name, _ => Task.FromResult<IEngine>(new QuickEngine()), requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NumberedContext(nextNumber())))
      .SetRestartFactory((_, _) => Task.FromResult<IEngine>(new QuickEngine()));

  /// <summary>
  /// A started coordinator with the engines <paramref name="names"/>, replaced by a generation switch that keeps only
  /// the engine <see cref="Name"/>.
  /// </summary>
  private static async Task<(EngineCoordinator Replaced, EngineCoordinator Next)> SwitchedAsync(
    bool useDispatcher, CancellationToken ct, params string[] names)
  {
    var calls = 0;
    int nextNumber() => Interlocked.Increment(ref calls);
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
    var (replaced, next) = await SwitchedAsync(useDispatcher, ct, Name);
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
    var (replaced, next) = await SwitchedAsync(useDispatcher, ct, Name);
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
    var (replaced, _) = await SwitchedAsync(useDispatcher, ct, Name, "Dropped");

    var invalidate = () => replaced.InvalidateContextForEngineAsync("Dropped", ct).WaitAsync(MustComplete, ct);
    var refresh = () => replaced.RefreshEngineContextAsync("Dropped", new NoContext(), ct).WaitAsync(MustComplete, ct);

    await invalidate.Should().ThrowAsync<ArgumentException>();
    await refresh.Should().ThrowAsync<ArgumentException>();
  }
}
