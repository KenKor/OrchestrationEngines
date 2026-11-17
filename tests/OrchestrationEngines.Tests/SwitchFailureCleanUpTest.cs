using System;
using System.Collections.Generic;
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
/// Contains tests for a generation switch (RestartWithDefinitionsAsync) that fails after it has created the next
/// coordinator, before it hands over to it: the switch cleans that coordinator up (shuts it down if it was started,
/// disposes it otherwise), throws its own failure, and the current generation stays current (#100).
/// </summary>
public class SwitchFailureCleanUpTest
{
  private const string Name = "N";

  /// <summary>
  /// An engine with one load task, whose work the test decides; counts its loads.
  /// </summary>
  private class NetworkEngine(Func<CancellationToken, Task> loadAsync) : BaseEngine(NullLogger.Instance)
  {
    private int _loads;

    public int Loads => Volatile.Read(ref _loads);

    protected override void SetUpTasks() => CreateTask("Load", token =>
    {
      Interlocked.Increment(ref _loads);
      return loadAsync(token);
    });
  }

  private static NetworkEngine Quick() => new(_ => Task.CompletedTask);

  /// <summary>
  /// An engine whose load runs until it is stopped.
  /// </summary>
  private static NetworkEngine Hanging() => new(token => Task.Delay(Timeout.Infinite, token));

  /// <summary>
  /// A coordinator whose next generation the test can see: the switch hands it over only when it succeeds.
  /// </summary>
  private static EngineCoordinator CreateCoordinator(List<EngineCoordinator> nextGenerations, ILogger logger = null,
    bool useDispatcher = false)
  {
    var coordinator = new EngineCoordinator(nameof(SwitchFailureCleanUpTest), logger ?? NullLogger.Instance,
      useDispatcher);
    coordinator.NextGenerationCreated = nextGenerations.Add;
    return coordinator;
  }

  /// <summary>
  /// A descriptor whose first engine is <paramref name="first"/> and whose next one, in a switch, comes from
  /// <paramref name="next"/>; without <paramref name="next"/> a switch builds the engine from scratch.
  /// </summary>
  private static EngineDescriptor Descriptor(IEngine first, Func<IEngine> next = null)
  {
    var descriptor = new EngineDescriptor(Name, _ => Task.FromResult(first), requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();
    return next == null ? descriptor : descriptor.SetRestartFactory((_, _) => Task.FromResult(next()));
  }

  /// <summary>
  /// Verifies that a switch whose engine rebuilt from scratch fails to stop throws that failure and disposes the next
  /// coordinator, which it did not start.
  /// </summary>
  [Fact]
  public async Task Switch_RebuiltEngineFailsToStop_ThrowsAndDisposesTheNextCoordinator()
  {
    var ct = TestContext.Current.CancellationToken;
    var failure = new InvalidOperationException("The stop failed.");
    var nextGenerations = new List<EngineCoordinator>();
    var coordinator = CreateCoordinator(nextGenerations);
    coordinator.RegisterEngineDescriptor(Descriptor(new StopFailingEngine(failure, _ => Task.CompletedTask)));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var thrown = await Record.ExceptionAsync(() =>
      coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct)
        .WaitAsync(MustComplete, ct));

    thrown.Should().BeSameAs(failure);
    nextGenerations.Should().ContainSingle();
    nextGenerations[0].IsDisposed.Should().BeTrue("the switch owned the next coordinator, and did not start it");
    nextGenerations[0].State.Should().Be(EngineCoordinatorState.Created);
    coordinator.Successor.Should().BeNull("the switch did not hand over");
  }

  /// <summary>
  /// Verifies that a switch whose next coordinator fails to start throws that failure and shuts the next coordinator
  /// down.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Switch_NextCoordinatorFailsToStart_ThrowsAndShutsItDown(bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    var current = Quick();
    var nextGenerations = new List<EngineCoordinator>();
    using var coordinator = CreateCoordinator(nextGenerations, useDispatcher: useDispatcher);
    coordinator.RegisterEngineDescriptor(Descriptor(current, Quick));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var candidates = new List<EngineDescriptor>(coordinator.GetRegisteredDefinitions())
    {
      new EngineDescriptor("Planned", _ => Task.FromResult<IEngine>(Quick()), requiredForRunning: true, 1)
        .AddDependsOn("NotRegistered"),
    };

    var thrown = await Record.ExceptionAsync(() =>
      coordinator.RestartWithDefinitionsAsync(candidates, waitUntilReady: true, ct).WaitAsync(MustComplete, ct));

    thrown.Should().BeOfType<KeyNotFoundException>("the next generation's task graph names an engine that is not registered");
    nextGenerations.Should().ContainSingle();
    nextGenerations[0].State.Should().Be(EngineCoordinatorState.Stopped, "the switch started the next coordinator");
    coordinator.Successor.Should().BeNull("the switch did not hand over");
    current.State.Should().Be(EngineState.Ready);
    (await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct)).Should().BeSameAs(current);
  }

  /// <summary>
  /// Verifies that a switch that waits until the next generation is ready, and whose wait is cancelled, throws the
  /// cancellation and shuts the next generation down: its engines stop, and the current generation's engines keep
  /// their state and are still the ones its lookups give.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Switch_ReadyWaitCancelled_ShutsTheNextGenerationDownAndKeepsTheCurrentOne(bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    var current = Quick();
    var next = Hanging();
    var nextGenerations = new List<EngineCoordinator>();
    using var coordinator = CreateCoordinator(nextGenerations, useDispatcher: useDispatcher);
    coordinator.RegisterEngineDescriptor(Descriptor(current, () => next));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

    var switching = coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true,
      cancel.Token);
    await Wait.UntilAsync(() => next.Loads == 1, MustComplete, "the next generation starts its engine");
    cancel.Cancel();
    var thrown = await Record.ExceptionAsync(() => switching.WaitAsync(MustComplete, ct));

    thrown.Should().BeAssignableTo<OperationCanceledException>();
    nextGenerations.Should().ContainSingle();
    nextGenerations[0].State.Should().Be(EngineCoordinatorState.Stopped);
    next.IsCancellationRequested.Should().BeTrue("the next generation's engines are stopped");
    coordinator.Successor.Should().BeNull("the switch did not hand over");
    current.State.Should().Be(EngineState.Ready);
    current.IsCancellationRequested.Should().BeFalse("the clean-up stops none of the current generation's engines");
    (await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct)).Should().BeSameAs(current);
  }

  /// <summary>
  /// Verifies that once a switch that rebuilds an engine from scratch fails in its ready-wait, a lookup of that engine
  /// on the current coordinator gives null, as it does when the next generation does not start, and is not answered
  /// by the next generation the switch shut down: with the dispatcher, whose calls that shutdown cancels, too.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Switch_RebuildingReadyWaitCancelled_LookupOfTheRebuiltEngineGivesNull(bool useDispatcher)
  {
    var ct = TestContext.Current.CancellationToken;
    var next = Hanging();
    var created = 0;
    var nextGenerations = new List<EngineCoordinator>();
    using var coordinator = CreateCoordinator(nextGenerations, useDispatcher: useDispatcher);
    coordinator.RegisterEngineDescriptor(new EngineDescriptor(Name,
        _ => Task.FromResult<IEngine>(Interlocked.Increment(ref created) == 1 ? Quick() : next), requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts());
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

    var switching = coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true,
      cancel.Token);
    await Wait.UntilAsync(() => next.Loads == 1, MustComplete, "the next generation starts the rebuilt engine");
    cancel.Cancel();
    (await Record.ExceptionAsync(() => switching.WaitAsync(MustComplete, ct))).Should().BeAssignableTo<OperationCanceledException>();

    nextGenerations[0].State.Should().Be(EngineCoordinatorState.Stopped);
    (await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct)).Should()
      .BeNull("the switch stopped the engine to rebuild it, and its next generation did not take over");
  }

  /// <summary>
  /// Verifies that a clean-up of the next generation that throws is logged, and that the switch still throws its own
  /// failure.
  /// </summary>
  [Fact]
  public async Task Switch_CleanUpThrows_LogsItAndThrowsTheSwitchFailure()
  {
    var ct = TestContext.Current.CancellationToken;
    var next = new StopFailingEngine(new InvalidOperationException("The next engine's stop failed."),
      token => Task.Delay(Timeout.Infinite, token));
    var logged = 0;
    var logger = new OnMessageLogger("Cleaning up the next generation", () => Interlocked.Increment(ref logged));
    var nextGenerations = new List<EngineCoordinator>();
    var coordinator = CreateCoordinator(nextGenerations, logger);
    coordinator.RegisterEngineDescriptor(Descriptor(Quick(), () => next));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

    var switching = coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true,
      cancel.Token);
    await Wait.UntilAsync(() => next.Loads == 1, MustComplete, "the next generation starts its engine");
    cancel.Cancel();
    var thrown = await Record.ExceptionAsync(() => switching.WaitAsync(MustComplete, ct));

    thrown.Should().BeAssignableTo<OperationCanceledException>("the switch's own failure, not the clean-up's");
    Volatile.Read(ref logged).Should().Be(1);
    nextGenerations[0].State.Should().Be(EngineCoordinatorState.Stopped);
  }
}
