using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for a coordinator generation switch (RestartWithDefinitionsAsync) and what it tells an engine's
/// restart factory. Hosts rely on this to let unchanged engines take over their previous state, and to release a
/// network by switching to a generation without it.
/// </summary>
public class EngineGenerationSwitchTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  private sealed class QuickEngine() : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Load", _ => Task.CompletedTask);
  }

  /// <summary>
  /// Records the restart input each engine's restart factory received.
  /// </summary>
  private sealed class RestartLog
  {
    public ConcurrentDictionary<string, EngineRestartInput> Inputs { get; } = new(StringComparer.OrdinalIgnoreCase);
  }

  private static EngineDescriptor Descriptor(string name, RestartLog log, int version = 1, string dependsOn = null)
  {
    var descriptor = new EngineDescriptor(name, _ => Task.FromResult<IEngine>(new QuickEngine()), requiredForRunning: true, version)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      .SetRestartFactory((input, _) =>
      {
        log.Inputs[input.Name] = input;
        return Task.FromResult<IEngine>(new QuickEngine());
      });
    if (dependsOn != null)
      descriptor.AddDependsOn(dependsOn);
    return descriptor;
  }

  /// <summary>
  /// Starts a coordinator (no dispatcher, as on .NET) with engine A and engine B depending on A, and waits until ready.
  /// </summary>
  private static async Task<EngineCoordinator> StartedCoordinatorAsync(RestartLog log)
  {
    var coordinator = new EngineCoordinator("EngineGenerationSwitchTest", NullLogger.Instance, useDispatcher: false);
    coordinator.RegisterEngineDescriptor(Descriptor("A", log));
    coordinator.RegisterEngineDescriptor(Descriptor("B", log, dependsOn: "A"));
    await coordinator.EnsureReadyAsync().WaitAsync(MustComplete, TestContext.Current.CancellationToken);
    return coordinator;
  }

  /// <summary>
  /// Verifies a switch with unchanged definitions hands every restart factory its previous engine with nothing
  /// changed, starts fresh instances and stops the previous ones.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RestartWithDefinitionsAsync_Unchanged_OffersPreviousEngineWithNothingChanged()
  {
    var ct = TestContext.Current.CancellationToken;
    var log = new RestartLog();
    var coordinator = await StartedCoordinatorAsync(log);
    var previousA = coordinator.Engines["A"];
    var previousB = coordinator.Engines["B"];

    var next = await coordinator.RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), waitUntilReady: true, ct)
      .WaitAsync(MustComplete, ct);

    log.Inputs["A"].NothingChanged.Should().BeTrue();
    log.Inputs["A"].PreviousEngine.Should().BeSameAs(previousA);
    log.Inputs["B"].NothingChanged.Should().BeTrue();
    log.Inputs["B"].PreviousEngine.Should().BeSameAs(previousB);
    next.Engines["A"].Should().NotBeSameAs(previousA);
    next.GetEngineIfReady("A").Should().NotBeNull();
    ((BaseEngine)previousA).IsCancellationRequested.Should().BeTrue("the previous generation's engines are stopped");
  }

  /// <summary>
  /// Verifies a version bump marks that engine's definition as changed and the engines depending on it as having a
  /// changed dependency, so neither takes over its previous state.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RestartWithDefinitionsAsync_VersionBump_MarksEngineAndDependentsChanged()
  {
    var ct = TestContext.Current.CancellationToken;
    var log = new RestartLog();
    var coordinator = await StartedCoordinatorAsync(log);
    var candidates = new[] { Descriptor("A", log, version: 2), Descriptor("B", log, dependsOn: "A") };

    await coordinator.RestartWithDefinitionsAsync(candidates, waitUntilReady: true, ct).WaitAsync(MustComplete, ct);

    log.Inputs["A"].DefinitionChanged.Should().BeTrue();
    log.Inputs["A"].NothingChanged.Should().BeFalse();
    log.Inputs["B"].DependencyChanged.Should().BeTrue();
    log.Inputs["B"].NothingChanged.Should().BeFalse();
  }

  /// <summary>
  /// Verifies a switch without an engine (a release) stops that engine and does not recreate it, while the others
  /// are offered their previous state.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RestartWithDefinitionsAsync_WithoutEngine_StopsItAndDoesNotRecreateIt()
  {
    var ct = TestContext.Current.CancellationToken;
    var log = new RestartLog();
    var coordinator = await StartedCoordinatorAsync(log);
    var previousB = coordinator.Engines["B"];
    var candidates = new[] { Descriptor("A", log) };

    var next = await coordinator.RestartWithDefinitionsAsync(candidates, waitUntilReady: true, ct).WaitAsync(MustComplete, ct);

    next.Engines.ContainsKey("B").Should().BeFalse();
    ((BaseEngine)previousB).IsCancellationRequested.Should().BeTrue("the released engine is stopped");
    log.Inputs["A"].NothingChanged.Should().BeTrue();
    log.Inputs.ContainsKey("B").Should().BeFalse();
  }
}
