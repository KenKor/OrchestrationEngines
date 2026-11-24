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
/// Contains tests for a lookup through the coordinator the host holds, made while a switch that waits until ready
/// runs: from inside an engine of the next generation, or from outside an engine (#154).
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
  /// looks up another engine through the coordinator the host holds. It gets the next generation's A, which the switch
  /// rebuilds from scratch. With <paramref name="waitForTask"/>,
  /// B waits for A's task through <see cref="EngineCoordinator.EnsureEngineTaskReadyAsync"/> before the lookup.
  /// </summary>
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task EngineCoordinator_SwitchWaitingUntilReady_LookupFromNextGenerationEngine_Completes(
    bool waitForTask)
  {
    var ct = TestContext.Current.CancellationToken;
    var lookups = 0;
    IEngine foundByNextB = null;
    var coordinator = new EngineCoordinator(nameof(LookupFromNextGenerationTest), NullLogger.Instance);
    coordinator.RegisterEngineDescriptor(Descriptor("A"));
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
  /// Verifies what a lookup from outside an engine gets when it lands during a switch that then fails while it waits
  /// until ready, for an engine the switch rebuilds from scratch: the next generation's engine, which the failed
  /// switch stops. A lookup after the failure gets null: the switch stopped A before the next generation started.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_HostLookupDuringSwitchThatFails_GetsAnEngineTheFailureStops()
  {
    var ct = TestContext.Current.CancellationToken;
    var cLoading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var coordinator = new EngineCoordinator(nameof(LookupFromNextGenerationTest), NullLogger.Instance);
    var a = Descriptor("A");
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
    foundAfter.Should().BeNull("the switch stopped the rebuilt A before its next generation started");
    await coordinator.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }
}
