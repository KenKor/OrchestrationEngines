using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for a lookup through the coordinator the host holds, made while a switch runs: from inside an engine
/// of the next generation, or from outside an engine. Until the switch hands over, the previous generation answers,
/// except for an engine the switch rebuilds from scratch.
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
  /// rebuilds from scratch.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_SwitchWaitingUntilReady_LookupFromNextGenerationEngine_Completes()
  {
    var ct = TestContext.Current.CancellationToken;
    var lookups = 0;
    IEngine foundByNextB = null;
    var coordinator = new EngineCoordinator(nameof(LookupFromNextGenerationTest), NullLogger.Instance);
    coordinator.RegisterEngineDescriptor(Descriptor("A"));
    coordinator.RegisterEngineDescriptor(Descriptor("B", async token =>
    {
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
  /// Verifies what an engine of the next generation gets when, while it loads, it looks up through the coordinator the
  /// host holds an engine the switch hands over (A has a restart factory): the previous generation's A, which the
  /// hand-over then stops. The switch waits until ready; in one that does not, the lookup races the hand-over, and
  /// gets either that engine or the next generation's.
  /// </summary>
  [Fact]
  public async Task
    EngineCoordinator_SwitchWaitingUntilReady_LookupOfHandedOverEngineFromNextGenerationEngine_GetsThePreviousOne()
  {
    var ct = TestContext.Current.CancellationToken;
    var lookups = 0;
    IEngine foundByNextB = null;
    var coordinator = new EngineCoordinator(nameof(LookupFromNextGenerationTest), NullLogger.Instance);
    var a = Descriptor("A");
    a.SetRestartFactory((_, _) => Task.FromResult<IEngine>(SwitchTestEngines.Quick()));
    coordinator.RegisterEngineDescriptor(a);
    coordinator.RegisterEngineDescriptor(Descriptor("B", async token =>
    {
      var found = await coordinator.GetEngineAsync("A", token).ConfigureAwait(false);
      if (Interlocked.Increment(ref lookups) > 1)
        Volatile.Write(ref foundByNextB, found);
    }));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var previousA = coordinator.GetEngineIfReady("A");

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(),
      waitUntilReady: true, ct).WaitAsync(MustComplete, ct);

    Volatile.Read(ref foundByNextB).Should().BeSameAs(previousA, "the previous generation answers until the hand-over")
      .And.NotBeSameAs(next.GetEngineIfReady("A"));
    await Wait.UntilAsync(() => previousA.State == EngineState.Stopped, MustComplete,
      "the hand-over stops the engine the lookup got");
    await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }

  /// <summary>
  /// Verifies what an engine of the next generation gets when, while it loads, it looks up through the coordinator the
  /// host holds an engine the switch adds (C), in a switch that waits until ready: null, as the previous generation
  /// answers until the hand-over and does not know C; a lookup that throws for an unknown engine throws. The switch
  /// still completes.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_SwitchWaitingUntilReady_LookupOfAddedEngineFromNextGenerationEngine_GetsNull()
  {
    var ct = TestContext.Current.CancellationToken;
    var looked = 0;
    IEngine foundByB = null;
    Exception thrownInB = null;
    var coordinator = new EngineCoordinator(nameof(LookupFromNextGenerationTest), NullLogger.Instance);
    coordinator.RegisterEngineDescriptor(Descriptor("A"));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var definitions = new List<EngineDescriptor>(coordinator.GetRegisteredDefinitions())
    {
      Descriptor("B", async token =>
      {
        Volatile.Write(ref foundByB, await coordinator.GetEngineAsync("C", token).ConfigureAwait(false));
        try
        {
          await coordinator.GetEngineAsync("C", throwErrorIfNotFound: true, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
          Volatile.Write(ref thrownInB, ex);
        }
        Interlocked.Increment(ref looked);
      }),
      Descriptor("C"),
    };

    var next = await coordinator.RestartWithDefinitionsAsync(definitions, waitUntilReady: true, ct)
      .WaitAsync(MustComplete, ct);

    Volatile.Read(ref looked).Should().Be(1);
    Volatile.Read(ref foundByB).Should().BeNull("the previous generation does not know C");
    Volatile.Read(ref thrownInB).Should().BeOfType<EvaluateException>();
    next.GetEngineIfReady("C").Should().NotBeNull();
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
