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
/// Contains tests for a timed lookup that a generation switch passes on to the next generation (#156): the next
/// generation gets the time the lookup has left, not its whole time-out again.
/// </summary>
public class LookupAcrossGenerationsTest
{
  private const string Name = "Hanging";

  /// <summary>An engine whose load does not end until the engine stops: it never gets Ready.</summary>
  private sealed class HangingEngine() : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Load", token => Task.Delay(Timeout.Infinite, token));
  }

  /// <summary>
  /// Verifies that a timed lookup waiting on an engine that a switch rebuilds goes on in the next generation with the
  /// time it has left: it ends at the deadline it started with.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_TimedLookupPassedToTheNextGeneration_KeepsItsDeadline()
  {
    var ct = TestContext.Current.CancellationToken;
    var clock = new ManualTimeProvider();
    var coordinator = new EngineCoordinator(nameof(LookupAcrossGenerationsTest), NullLogger.Instance, useDispatcher: false)
    {
      TimeProvider = clock,
    };
    coordinator.RegisterEngine(Name, _ => Task.FromResult<IEngine>(new HangingEngine()))
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()));
    await coordinator.StartAsync().WaitAsync(MustComplete, ct);
    var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(10);

    var lookup = coordinator.GetEngineAsync(Name, (int)TimeSpan.FromMinutes(10).TotalMilliseconds, ct);
    await Wait.UntilAsync(() => clock.DueTimesSince(0).Contains(deadline), MustComplete, "the lookup must be waiting");
    clock.Advance(TimeSpan.FromMinutes(9));
    var timersBeforeSwitch = clock.TimersCreated;
    var next = await coordinator
      .RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: false, ct)
      .WaitAsync(MustComplete, ct);

    await Wait.UntilAsync(() => lookup.IsCompleted || clock.DueTimesSince(timersBeforeSwitch).Contains(deadline),
      MustComplete,
      "the next generation must wait until the lookup's first deadline, not a whole time-out after the switch");
    lookup.IsCompleted.Should().BeFalse("the engine never gets Ready and the deadline has not passed");
    clock.Advance(TimeSpan.FromMinutes(1));
    (await lookup.WaitAsync(MustComplete, ct)).Should().BeNull();

    await next.ShutdownAsync(ct).WaitAsync(MustComplete, ct);
  }
}
