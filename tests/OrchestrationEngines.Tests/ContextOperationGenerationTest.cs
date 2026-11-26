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
/// Contains tests for context operations on a coordinator that a generation switch replaced, which hand off to the
/// newer generation (#86, item 1), and for a task reset during a switch, before it hands over, which waits for the next
/// generation and acts there (#123, #145). A refresh in that window is not tested: it can reach the next generation
/// before that generation has created the engine (a known defect, see RefreshEngineContextAsync).
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
    CancellationToken ct, params string[] names)
  {
    var calls = 0;
    int nextNumber() => Interlocked.Increment(ref calls);
    var coordinator = new EngineCoordinator(nameof(ContextOperationGenerationTest), NullLogger.Instance);
    foreach (var name in names)
      coordinator.RegisterEngineDescriptor(Descriptor(name, nextNumber));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var kept = coordinator.GetRegisteredDefinitions().Where(d => d.Name == Name).ToList();
    var next = await coordinator.SwitchUntilReadyAsync(kept, ct)
      .WaitAsync(MustComplete, ct);
    next.Should().NotBeSameAs(coordinator);
    coordinator.Successor.Should().BeSameAs(next);
    return (coordinator, next);
  }

  /// <summary>
  /// Verifies that refreshing a context on a replaced coordinator pushes it into the newer generation's engine.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RefreshEngineContext_ReplacedCoordinator_PushesIntoTheNextGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    var (replaced, next) = await SwitchedAsync(ct, Name);
    var fresh = new NumberedContext(-1);

    await replaced.RefreshEngineContextAsync(Name, fresh, ct).WaitAsync(MustComplete, ct);

    Own(next.Engines[Name]).Should().BeSameAs(fresh);
    next.State.Should().Be(EngineCoordinatorState.Running);
  }

  /// <summary>
  /// Verifies that a context operation on a replaced coordinator, for an engine the newer generation does not have,
  /// fails as for an unknown name.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_ContextOperations_ReplacedCoordinatorEngineDropped_ThrowArgumentException()
  {
    var ct = TestContext.Current.CancellationToken;
    var (replaced, _) = await SwitchedAsync(ct, Name, "Dropped");

    var refresh = () => replaced.RefreshEngineContextAsync("Dropped", new NoContext(), ct).WaitAsync(MustComplete, ct);

    await refresh.Should().ThrowAsync<ArgumentException>();
  }

  /// <summary>
  /// A generation switch held before it starts its next generation, while it stops engine B, which it rebuilds from
  /// scratch; engine A it hands over to its restart factory.
  /// </summary>
  private sealed class HeldSwitch
  {
    private readonly List<LoadEngine> _instancesOfA = [];
    private readonly StopFailingEngine _previousB = new(failure: null, holdStop: true);

    public EngineCoordinator Coordinator { get; }

    public Task<EngineCoordinator> Switch { get; private set; }

    public HeldSwitch()
    {
      Coordinator = new EngineCoordinator(nameof(ContextOperationGenerationTest), NullLogger.Instance);
      Coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(NewA()))
        .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
        .SetRestartFactory((_, _) => Task.FromResult<IEngine>(NewA()))
        .WithoutRestarts();
      var instancesOfB = 0;
      Coordinator.RegisterEngine("B", _ => Task.FromResult<IEngine>(
          Interlocked.Increment(ref instancesOfB) == 1 ? (IEngine)_previousB : new LoadEngine()))
        .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
        .WithoutRestarts();
    }

    private LoadEngine NewA()
    {
      var engine = new LoadEngine();
      lock (_instancesOfA)
        _instancesOfA.Add(engine);
      return engine;
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
      Switch = Task.Run(() => Coordinator.RestartWithDefinitionsAsync(Coordinator.GetRegisteredDefinitions()), ct);
      await _previousB.Stopping.Task.WaitAsync(MustComplete, ct);
      NextA.Should().BeNull("the switch has not started the next generation");
    }

    /// <summary>
    /// Releases the held stop and starts <paramref name="operation"/> before: it must wait for the next generation.
    /// </summary>
    public async Task<EngineCoordinator> ReleaseAsync(Task operation, CancellationToken ct)
    {
      await Task.Delay(MustStayPending, ct);
      operation.IsCompleted.Should().BeFalse("the operation waits for the switch's next generation");
      _previousB.ReleaseStop.SetResult();
      await operation.WaitAsync(MustComplete, ct);
      var next = await Switch.WaitAsync(MustComplete, ct);
      await next.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
      return next;
    }
  }

  /// <summary>
  /// Verifies that a task reset that arrives during a switch, before it hands over, of an engine the switch hands over
  /// to its restart factory, resets the next generation's engine: the previous one does not load again (#145).
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_ResetTasksForEngine_BeforeHandoverWithRestartFactory_ResetsTheNextGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    var held = new HeldSwitch();
    await held.StartAndHoldAsync(ct);
    var previousLoads = held.PreviousA.Loads;

    var next = await held.ReleaseAsync(Task.Run(() =>
      held.Coordinator.ResetTasksForEngineAsync("A", ["Load"], resetDependentTasks: false, ct), ct), ct);

    await Wait.UntilAsync(() => held.NextA.Loads == 2, MustComplete, "the next generation's A loads again");
    held.PreviousA.Loads.Should().Be(previousLoads, "the previous A is not loaded again");
    await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that a refresh that arrives while a switch waits for the next generation is not left waiting when the
  /// switch fails before it starts that generation: this generation answers, as without the switch. The refreshed
  /// engine is the one the switch rebuilds from scratch, or, with <paramref name="handedOver"/>, one it hands over to
  /// its restart factory (#145).
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task EngineCoordinator_RefreshEngineContext_DuringSwitchThatDoesNotStart_RefreshesThisGeneration(
    bool handedOver)
  {
    var ct = TestContext.Current.CancellationToken;
    var failure = new InvalidOperationException("The stop failed.");
    var engine = new StopFailingEngine(failure, holdStop: true);
    var handedOverA = new LoadEngine();
    var coordinator = new EngineCoordinator(nameof(ContextOperationGenerationTest), NullLogger.Instance);
    coordinator.RegisterEngine(Name, _ => Task.FromResult<IEngine>(engine))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(handedOverA))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .SetRestartFactory((_, _) => Task.FromResult<IEngine>(new LoadEngine()))
      .WithoutRestarts();
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var @switch = Task.Run(() => coordinator.SwitchUntilReadyAsync(coordinator.GetRegisteredDefinitions(), ct), ct);
    await engine.Stopping.Task.WaitAsync(MustComplete, ct);
    var context = new NoContext();

    var refresh = Task.Run(() => coordinator.RefreshEngineContextAsync(handedOver ? "A" : Name, context, ct), ct);
    await Task.Delay(MustStayPending, ct);
    var pendingWhileStopping = !refresh.IsCompleted;
    engine.ReleaseStop.SetResult();

    pendingWhileStopping.Should().BeTrue("the refresh waits for the switch's next generation");
    (await Record.ExceptionAsync(() => @switch.WaitAsync(MustComplete, ct))).Should().BeSameAs(failure);
    await refresh.WaitAsync(MustComplete, ct);
    (handedOver ? handedOverA.Context : engine.Context).Should()
      .BeSameAs(context, "the switch did not start the next generation: this one answers");
    (await Record.ExceptionAsync(() => coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct)))
      .Should().BeSameAs(failure, "the shutdown stops the engine again, and its stop fails again");
  }
}
