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
  /// <summary>
  /// A required descriptor named <paramref name="name"/> whose engine's load task runs <paramref name="load"/>, or ends
  /// at once without it.
  /// </summary>
  private static EngineDescriptor Descriptor(string name, Func<CancellationToken, Task> load = null) =>
    new EngineDescriptor(name, _ => Task.FromResult<IEngine>(load == null
        ? SwitchTestEngines.Quick()
        : new SwitchTestEngines.NetworkEngine(load)), requiredForRunning: true, 1)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .WithoutRestarts();

  /// <summary>
  /// Verifies that a switch that waits until ready completes when an engine of the next generation, while it loads,
  /// looks up another engine through the coordinator the host holds: with the dispatcher on, that lookup must not
  /// wait behind the switch on the replaced generation's dispatcher. It gets the next generation's A, whether A is
  /// rebuilt from scratch or, with <paramref name="handOverA"/>, handed over to its restart factory. With
  /// <paramref name="waitForTask"/>,
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
    var a = Descriptor("A");
    if (handOverA)
      a.SetRestartFactory((_, _) => Task.FromResult<IEngine>(SwitchTestEngines.Quick()));
    coordinator.RegisterEngineDescriptor(a);
    coordinator.RegisterEngineDescriptor(Descriptor("B", async token =>
    {
      if (waitForTask)
        await coordinator.EnsureEngineTaskReadyAsync("A", "Load", token).ConfigureAwait(false);
      var found = await coordinator.GetEngineAsync("A", token).ConfigureAwait(false);
      if (Interlocked.Increment(ref lookups) > 1)
        Volatile.Write(ref foundByNextB, found);
    }));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(),
      waitUntilReady: true, ct).WaitAsync(MustComplete, ct);

    next.Should().NotBeNull().And.NotBeSameAs(coordinator);
    Volatile.Read(ref foundByNextB).Should().NotBeNull().And.BeSameAs(next.GetEngineIfReady("A"),
      "the next generation's B finds the next generation's A");
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
    coordinator.RegisterEngineDescriptor(Descriptor("A"));
    coordinator.RegisterEngineDescriptor(Descriptor("B", async token =>
      Volatile.Write(ref foundByB, await coordinator.GetEngineAsync("A", token).ConfigureAwait(false))));

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
    coordinator.RegisterEngineDescriptor(Descriptor("A"));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var definitions = new List<EngineDescriptor>(coordinator.GetRegisteredDefinitions())
    {
      Descriptor("B", async token =>
        Volatile.Write(ref foundByB, await coordinator.GetEngineAsync("C", token).ConfigureAwait(false))),
      Descriptor("C", async token =>
      {
        cLoading.TrySetResult();
        await releaseC.Task.WaitAsync(token).ConfigureAwait(false);
      }),
    };

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

  /// <summary>
  /// Verifies that a lookup from outside an engine, during a switch that waits until ready, gets what it got when it
  /// waited for the switch to end: the next generation's engine for one the switch hands over (not the previous one,
  /// which the hand-over stops), and null for one the switch leaves out.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_HostLookupDuringSwitch_GetsTheNextGenerationsEngineOrNull()
  {
    var ct = TestContext.Current.CancellationToken;
    var cLoading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new EngineCoordinator(nameof(LookupFromNextGenerationTest), NullLogger.Instance,
      useDispatcher: true);
    var a = Descriptor("A").SetRestartFactory((_, _) => Task.FromResult<IEngine>(SwitchTestEngines.Quick()));
    coordinator.RegisterEngineDescriptor(a);
    coordinator.RegisterEngineDescriptor(Descriptor("Removed"));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var definitions = new List<EngineDescriptor>
    {
      a,
      Descriptor("C", async token =>
      {
        cLoading.TrySetResult();
        await releaseC.Task.WaitAsync(token).ConfigureAwait(false);
      }),
    };

    var @switch = coordinator.RestartWithDefinitionsAsync(definitions, waitUntilReady: true, ct);
    await cLoading.Task.WaitAsync(MustComplete, ct);
    var lookupA = coordinator.GetEngineAsync("A", false, ct);
    var lookupRemoved = coordinator.GetEngineAsync("Removed", false, ct);
    releaseC.SetResult();
    var next = await @switch.WaitAsync(MustComplete, ct);

    var foundA = await lookupA.WaitAsync(MustComplete, ct);
    foundA.Should().NotBeNull().And.BeSameAs(next.GetEngineIfReady("A"));
    foundA.State.Should().Be(EngineState.Ready);
    (await lookupRemoved.WaitAsync(MustComplete, ct)).Should().BeNull();
    await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies what a lookup from outside an engine gets when it lands during a switch that then fails while it waits
  /// until ready: the next generation's engine, which the failed switch stops. The owner chose this on 2026-11-24
  /// (#154, option (a)) over a deadlock for an engine of the next generation, which the lookup cannot tell apart
  /// from the host. A lookup after the failure gets this generation's engine: the previous A if the switch hands it
  /// over, null if it rebuilds A from scratch (stopped before the next generation started).
  /// </summary>
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task EngineCoordinator_HostLookupDuringSwitchThatFails_GetsAnEngineTheFailureStops(bool handOverA)
  {
    var ct = TestContext.Current.CancellationToken;
    var cLoading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new EngineCoordinator(nameof(LookupFromNextGenerationTest), NullLogger.Instance,
      useDispatcher: true);
    var a = Descriptor("A");
    if (handOverA)
      a.SetRestartFactory((_, _) => Task.FromResult<IEngine>(SwitchTestEngines.Quick()));
    coordinator.RegisterEngineDescriptor(a);
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var previousA = coordinator.GetEngineIfReady("A");
    var definitions = new List<EngineDescriptor>
    {
      a,
      Descriptor("C", async token =>
      {
        cLoading.TrySetResult();
        await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
      }),
    };
    using var switchCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

    var @switch = coordinator.RestartWithDefinitionsAsync(definitions, waitUntilReady: true, switchCancel.Token);
    await cLoading.Task.WaitAsync(MustComplete, ct);
    var foundDuringSwitch = await coordinator.GetEngineAsync("A", false, ct).WaitAsync(MustComplete, ct);
    switchCancel.Cancel();
    await @switch.Invoking(s => s.WaitAsync(MustComplete, ct)).Should().ThrowAsync<OperationCanceledException>();

    foundDuringSwitch.Should().NotBeNull().And.NotBeSameAs(previousA, "the next generation answered it");
    await Wait.UntilAsync(() => foundDuringSwitch.State == EngineState.Stopped, MustComplete,
      "the failed switch stops the next generation's engines");
    var foundAfter = await coordinator.GetEngineAsync("A", false, ct).WaitAsync(MustComplete, ct);
    if (handOverA)
      foundAfter.Should().BeSameAs(previousA).And.Match<IEngine>(e => e.State == EngineState.Ready);
    else
      foundAfter.Should().BeNull("the switch stopped the rebuilt A before its next generation started");
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }
}
