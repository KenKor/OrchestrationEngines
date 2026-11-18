using System;
using System.Collections.Generic;
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
/// replaced, which hand off to the newer generation (#86, item 1), or during a switch that rebuilds the engine from
/// scratch, before it hands over, which wait for the next generation and act there (#123).
/// </summary>
public class ContextOperationGenerationTest
{
  private const string Name = "N";

  private static readonly TimeSpan MustStayPending = TimeSpan.FromMilliseconds(500);

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
  /// An engine with one load task that runs <c>load</c>, or ends at once without it; counts its loads.
  /// </summary>
  private sealed class LoadEngine(Func<Task> load = null) : BaseEngine(NullLogger.Instance)
  {
    private int _loads;

    public int Loads => Volatile.Read(ref _loads);

    protected override void SetUpTasks() => CreateTask("Load", async _ =>
    {
      Interlocked.Increment(ref _loads);
      if (load != null)
        await load().ConfigureAwait(false);
    });
  }

  /// <summary>
  /// A required engine that loads at once, whose context factory numbers each context it makes, and whose generation
  /// switch makes a new engine.
  /// </summary>
  private static EngineDescriptor Descriptor(string name, Func<int> nextNumber) =>
    new EngineDescriptor(name, _ => Task.FromResult<IEngine>(new LoadEngine()), requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NumberedContext(nextNumber())))
      .SetRestartFactory((_, _) => Task.FromResult<IEngine>(new LoadEngine()));

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
      Coordinator = new EngineCoordinator(nameof(ContextOperationGenerationTest), NullLogger.Instance,
        useDispatcher: false);
      Coordinator.RegisterEngine("A", _ =>
        {
          var engine = new LoadEngine();
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

  /// <summary>
  /// Verifies that a refresh that arrives while a switch rebuilds its engine, and waits for the next generation, is not
  /// left waiting when the switch fails before it starts that generation: this generation answers, as without the
  /// switch.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RefreshEngineContext_DuringSwitchThatDoesNotStart_RefreshesThisGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    var failure = new InvalidOperationException("The stop failed.");
    var engine = new StopFailingEngine(failure, holdStop: true);
    var coordinator = new EngineCoordinator(nameof(ContextOperationGenerationTest), NullLogger.Instance,
      useDispatcher: false);
    coordinator.RegisterEngine(Name, _ => Task.FromResult<IEngine>(engine))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var @switch = Task.Run(() => coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(),
      waitUntilReady: true, ct), ct);
    await engine.Stopping.Task.WaitAsync(MustComplete, ct);
    var context = new NoContext();

    var refresh = Task.Run(() => coordinator.RefreshEngineContextAsync(Name, context, ct), ct);
    await Task.Delay(MustStayPending, ct);
    var pendingWhileStopping = !refresh.IsCompleted;
    engine.ReleaseStop.SetResult();

    pendingWhileStopping.Should().BeTrue("the refresh waits for the switch's next generation");
    (await Record.ExceptionAsync(() => @switch.WaitAsync(MustComplete, ct))).Should().BeSameAs(failure);
    await refresh.WaitAsync(MustComplete, ct);
    engine.Context.Should().BeSameAs(context, "the switch did not start the next generation: this one answers");
    (await Record.ExceptionAsync(() => coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct)))
      .Should().BeSameAs(failure, "the shutdown stops the engine again, and its stop fails again");
  }
}
