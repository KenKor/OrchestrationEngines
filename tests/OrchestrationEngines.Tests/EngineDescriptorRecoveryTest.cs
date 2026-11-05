using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Mechanic;
using OrchestrationEngines.Restarter;
using OrchestrationEngines.Tasks;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the recovery enforcers that the lifecycle tasks of an engine (creation, context, kick-start) get
/// from its descriptor. An enforcer keeps the restart count of one task, so tasks must not share one created from a
/// policy.
/// </summary>
public class EngineDescriptorRecoveryTest
{
  private sealed class IdleEngine() : BaseEngine(NullLogger.Instance)
  {
    protected override void SetUpTasks() => CreateTask("Load", _ => Task.CompletedTask);
  }

  private static readonly Func<CancellationToken, Task<IEngine>> Factory = _ => Task.FromResult<IEngine>(new IdleEngine());

  private static EngineTask[] LifecycleTasks(EngineDescriptor descriptor)
  {
    var mechanic = new MechanicEngine(NullLogger.Instance);
    return
    [
      new EngineCreationTask(mechanic, descriptor, NullLogger.Instance, CancellationToken.None),
      new EngineContextTask(mechanic, descriptor, NullLogger.Instance, CancellationToken.None),
      new EngineKickStartTask(mechanic, descriptor, NullLogger.Instance, CancellationToken.None),
    ];
  }

  /// <summary>
  /// Verifies that each lifecycle task gets an enforcer of its own when the descriptor's recovery was set from a policy.
  /// </summary>
  [Fact]
  public void EngineDescriptor_SetRecoveryHelperFromPolicy_EachLifecycleTaskHasItsOwnEnforcer()
  {
    var descriptor = new EngineDescriptor("Network", Factory).SetRecoveryHelper(RestartPolicy.Default);

    var helpers = LifecycleTasks(descriptor).Select(t => t.RecoveryHelper).ToArray();

    helpers.Should().AllBeOfType<RestartPolicyEnforcer>();
    helpers.Distinct().Should().HaveCount(3);
    helpers.Should().NotContain(descriptor.RecoveryHelper);
  }

  /// <summary>
  /// Verifies that an enforcer set directly is shared by the lifecycle tasks, as there is no telling how to create
  /// another, and that it replaces a policy set before.
  /// </summary>
  [Fact]
  public void EngineDescriptor_SetRecoveryHelperDirectly_TheLifecycleTasksShareIt()
  {
    var enforcer = RestartPolicy.Default.GetEnforcer();
    var descriptor = new EngineDescriptor("Network", Factory)
      .SetRecoveryHelper(RestartPolicy.Unlimited)
      .SetRecoveryHelper(enforcer);

    var helpers = LifecycleTasks(descriptor).Select(t => t.RecoveryHelper).ToArray();

    helpers.Should().AllSatisfy(h => h.Should().BeSameAs(enforcer));
  }

  /// <summary>
  /// Verifies that the next generation's descriptor, a copy made in a generation switch, still creates an enforcer of
  /// its own for each task instead of carrying the previous generation's over.
  /// </summary>
  [Fact]
  public async Task EngineCoordinator_RestartWithDefinitionsAsync_NextGenerationTasksGetEnforcersOfTheirOwn()
  {
    var ct = TestContext.Current.CancellationToken;
    var coordinator = new EngineCoordinator(nameof(EngineDescriptorRecoveryTest), NullLogger.Instance, useDispatcher: false);
    coordinator.RegisterEngine("Network", Factory, requiredForRunning: true).SetRecoveryHelper(RestartPolicy.Default);

    var next = await coordinator
      .RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), token: ct)
      .WaitAsync(TimeSpan.FromSeconds(5), ct);
    var copy = next.GetRegisteredDefinitions().Single();

    var helpers = LifecycleTasks(copy).Select(t => t.RecoveryHelper).ToArray();
    helpers.Distinct().Should().HaveCount(3);
    helpers.Should().NotContain(copy.RecoveryHelper);
  }
}
