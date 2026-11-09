using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for independent networks in one coordinator: engines that do not depend on each other must not
/// wait for, or fail with, each other.
/// </summary>
public class IndependentNetworksTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// A network engine with one load task, whose work the test decides.
  /// </summary>
  private sealed class NetworkEngine(Func<CancellationToken, Task> loadAsync) : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Load", loadAsync);
  }

  private static EngineCoordinator CreateCoordinator() =>
    new(nameof(IndependentNetworksTest), NullLogger.Instance, useDispatcher: false);

  private static void Register(EngineCoordinator coordinator, string name, IEngine engine) =>
    coordinator.RegisterEngine(name, _ => Task.FromResult(engine), requiredForRunning: true)
      .SetContextFactory(_ => Task.FromResult<IEngineContext>(new NoContext()))
      // A failed load must fault at once, not retry by the default restart policy.
      .WithoutRestarts();

  /// <summary>
  /// Verifies that a network is returned when an independent network in the same coordinator failed to load.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_GetEngineAsync_OtherIndependentNetworkFails_StillReturnsThisNetwork()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var north = new NetworkEngine(_ => throw new InvalidOperationException("The load failed."));
    var south = new NetworkEngine(_ => Task.CompletedTask);
    Register(coordinator, "North", north);
    Register(coordinator, "South", south);

    var failed = await coordinator.GetEngineAsync("North", ct).WaitAsync(MustComplete, ct);
    var loaded = await coordinator.GetEngineAsync("South", ct).WaitAsync(MustComplete, ct);

    failed.Should().BeNull();
    loaded.Should().BeSameAs(south);
  }

  /// <summary>
  /// Verifies that a network is returned while an independent network in the same coordinator is still loading.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_IndependentNetworks_LoadWithoutWaitingForEachOther()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = CreateCoordinator();
    var northGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var north = new NetworkEngine(token => northGate.Task.WaitAsync(token));
    var south = new NetworkEngine(_ => Task.CompletedTask);
    Register(coordinator, "North", north);
    Register(coordinator, "South", south);
    var northLookup = coordinator.GetEngineAsync("North", ct);

    var loaded = await coordinator.GetEngineAsync("South", ct).WaitAsync(MustComplete, ct);

    loaded.Should().BeSameAs(south);
    northLookup.IsCompleted.Should().BeFalse("North is still loading");
    northGate.SetResult();
    (await northLookup.WaitAsync(MustComplete, ct)).Should().BeSameAs(north);
  }
}
