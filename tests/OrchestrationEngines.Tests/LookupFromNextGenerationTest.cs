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
/// Contains tests for a lookup through the coordinator the host holds, made from inside an engine of the next
/// generation while a switch that waits until ready runs (#154).
/// </summary>
public class LookupFromNextGenerationTest
{
  /// <summary>An engine whose one load task runs <paramref name="load"/>, or ends at once without it.</summary>
  private sealed class LoadEngine(Func<CancellationToken, Task> load = null) : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Load", load ?? (_ => Task.CompletedTask));
  }

  /// <summary>
  /// Verifies that a switch that waits until ready completes when an engine of the next generation, while it loads,
  /// looks up another engine through the coordinator the host holds: with the dispatcher on, that lookup must not
  /// wait behind the switch on the replaced generation's dispatcher. Engine A is rebuilt from scratch: the lookup gets
  /// the next generation's A. Or, with <paramref name="handOverA"/>, A is handed over to its restart factory: as any
  /// lookup before the hand-over, it gets the previous A, which still runs then. With <paramref name="waitForTask"/>,
  /// B waits for A's task through <see cref="EngineCoordinator.EnsureEngineTaskReadyAsync"/> before the lookup.
  /// </summary>
  [Theory]
  [InlineData(false, false)]
  [InlineData(true, false)]
  [InlineData(false, true)]
  [InlineData(true, true)]
  public async Task EngineCoordinator_SwitchWaitingUntilReady_LookupFromNextGenerationEngine_Completes(bool handOverA,
    bool waitForTask)
  {
    var ct = TestContext.Current.CancellationToken;
    var lookups = 0;
    IEngine foundByNextB = null;
    var coordinator = new EngineCoordinator(nameof(LookupFromNextGenerationTest), NullLogger.Instance,
      useDispatcher: true);
    var a = coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(new LoadEngine()))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();
    if (handOverA)
      a.SetRestartFactory((_, _) => Task.FromResult<IEngine>(new LoadEngine()));
    coordinator.RegisterEngine("B", _ => Task.FromResult<IEngine>(new LoadEngine(async token =>
      {
        if (waitForTask)
          await coordinator.EnsureEngineTaskReadyAsync("A", "Load", token).ConfigureAwait(false);
        var found = await coordinator.GetEngineAsync("A", token).ConfigureAwait(false);
        if (Interlocked.Increment(ref lookups) > 1)
          Volatile.Write(ref foundByNextB, found);
      })))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var previousA = coordinator.GetEngineIfReady("A");

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(),
      waitUntilReady: true, ct).WaitAsync(MustComplete, ct);

    next.Should().NotBeNull().And.NotBeSameAs(coordinator);
    var expected = handOverA ? previousA : next.GetEngineIfReady("A");
    Volatile.Read(ref foundByNextB).Should().NotBeNull().And.BeSameAs(expected,
      "a lookup before the hand-over gets the engine that runs then");
    await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that a switch on a coordinator that was never started completes when an engine of the next generation
  /// looks up another engine through that coordinator: the lookup does not wait on the generation that never started.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_SwitchOnUnstartedCoordinator_LookupFromNextGenerationEngine_Completes()
  {
    var ct = TestContext.Current.CancellationToken;
    IEngine foundByB = null;
    var coordinator = new EngineCoordinator(nameof(LookupFromNextGenerationTest), NullLogger.Instance,
      useDispatcher: true);
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(new LoadEngine()))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();
    coordinator.RegisterEngine("B", _ => Task.FromResult<IEngine>(new LoadEngine(async token =>
        Volatile.Write(ref foundByB, await coordinator.GetEngineAsync("A", token).ConfigureAwait(false)))))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(),
      waitUntilReady: true, ct).WaitAsync(MustComplete, ct);

    Volatile.Read(ref foundByB).Should().NotBeNull().And.BeSameAs(next.GetEngineIfReady("A"));
    await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies that a switch that adds engine C completes when its engine B, new too, looks up C through the
  /// coordinator the host holds, and that a lookup of C from outside an engine during the switch gets the next
  /// generation's C, as it did when it waited for the switch to end.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_SwitchAddingEngines_LookupOfAddedEngine_AnsweredByTheNextGeneration()
  {
    var ct = TestContext.Current.CancellationToken;
    IEngine foundByB = null;
    var cLoading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new EngineCoordinator(nameof(LookupFromNextGenerationTest), NullLogger.Instance,
      useDispatcher: true);
    coordinator.RegisterEngine("A", _ => Task.FromResult<IEngine>(new LoadEngine()))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var definitions = new List<EngineDescriptor>(coordinator.GetRegisteredDefinitions());
    definitions.Add(new EngineDescriptor("B", _ => Task.FromResult<IEngine>(new LoadEngine(async token =>
          Volatile.Write(ref foundByB, await coordinator.GetEngineAsync("C", token).ConfigureAwait(false)))),
        requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts());
    definitions.Add(new EngineDescriptor("C", _ => Task.FromResult<IEngine>(new LoadEngine(async token =>
        {
          cLoading.TrySetResult();
          await releaseC.Task.WaitAsync(token).ConfigureAwait(false);
        })),
        requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts());

    var @switch = coordinator.RestartWithDefinitionsAsync(definitions, waitUntilReady: true, ct);
    await cLoading.Task.WaitAsync(MustComplete, ct);
    var hostLookup = coordinator.GetEngineAsync("C", ct);
    releaseC.SetResult();
    var next = await @switch.WaitAsync(MustComplete, ct);

    var foundByHost = await hostLookup.WaitAsync(MustComplete, ct);
    foundByHost.Should().NotBeNull().And.BeSameAs(next.GetEngineIfReady("C"));
    Volatile.Read(ref foundByB).Should().BeSameAs(foundByHost);
    await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }
}
