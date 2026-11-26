using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;
using static OrchestrationEngines.Tests.SwitchTestEngines;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for a generation switch (RestartWithDefinitionsAsync) that fails after it has created the next
/// coordinator, before it hands over to it: the switch cleans that coordinator up (shuts it down if it was started,
/// disposes it otherwise), throws its own failure, and the current generation stays current (#100).
/// </summary>
public class SwitchFailureCleanUpTest
{
  /// <summary>
  /// A coordinator whose next generation the test can see: the switch hands it over only when it succeeds.
  /// </summary>
  private static EngineCoordinator CreateCoordinator(List<EngineCoordinator> nextGenerations)
  {
    var coordinator = new EngineCoordinator(nameof(SwitchFailureCleanUpTest), NullLogger.Instance);
    coordinator.NextGenerationCreated = nextGenerations.Add;
    return coordinator;
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
      coordinator.SwitchUntilReadyAsync(coordinator.GetRegisteredDefinitions(), ct)
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
  [Fact]
  public async Task Switch_NextCoordinatorFailsToStart_ThrowsAndShutsItDown()
  {
    var ct = TestContext.Current.CancellationToken;
    var current = Quick();
    var nextGenerations = new List<EngineCoordinator>();
    using var coordinator = CreateCoordinator(nextGenerations);
    coordinator.RegisterEngineDescriptor(Descriptor(current, Quick));
    await coordinator.EnsureReadyAsync(ct).WaitAsync(MustComplete, ct);
    var candidates = new List<EngineDescriptor>(coordinator.GetRegisteredDefinitions())
    {
      Unstartable(),
    };

    var thrown = await Record.ExceptionAsync(() =>
      coordinator.SwitchUntilReadyAsync(candidates, ct).WaitAsync(MustComplete, ct));

    thrown.Should().BeOfType<KeyNotFoundException>("the next generation's task graph names an engine that is not registered");
    nextGenerations.Should().ContainSingle();
    nextGenerations[0].State.Should().Be(EngineCoordinatorState.Stopped, "the switch started the next coordinator");
    coordinator.Successor.Should().BeNull("the switch did not hand over");
    current.State.Should().Be(EngineState.Ready);
    (await coordinator.GetEngineAsync(Name, ct).WaitAsync(MustComplete, ct)).Should().BeSameAs(current);
  }

}
