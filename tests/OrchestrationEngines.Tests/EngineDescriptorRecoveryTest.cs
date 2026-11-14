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
  /// Verifies that each lifecycle task gets an enforcer of its own from the descriptor's
  /// <see cref="EngineDescriptor.RestartPolicy"/>: the default one, or one set with
  /// <see cref="EngineDescriptor.SetRestartPolicy"/>.
  /// </summary>
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void EngineDescriptor_RecoveryFromAPolicy_EachLifecycleTaskHasItsOwnEnforcer(bool setRestartPolicy)
  {
    var descriptor = new EngineDescriptor("Network", Factory);
    if (setRestartPolicy)
      descriptor.SetRestartPolicy(RestartPolicy.Critical);

    var helpers = LifecycleTasks(descriptor).Select(t => t.RecoveryHelper).ToArray();

    helpers.Should().AllBeOfType<RestartPolicyEnforcer>();
    helpers.Distinct().Should().HaveCount(3);
  }

  /// <summary>
  /// Verifies that <see cref="EngineDescriptor.SetRestartPolicy"/> sets <see cref="EngineDescriptor.RestartPolicy"/>,
  /// the one public way to set a policy (#59, item 4), and that it checks the policy as the property does.
  /// </summary>
  [Fact]
  public void EngineDescriptor_SetRestartPolicy_SetsTheRestartPolicy()
  {
    var descriptor = new EngineDescriptor("Network", Factory);
    var policy = RestartPolicy.Critical;

    descriptor.SetRestartPolicy(policy).Should().BeSameAs(descriptor);

    descriptor.RestartPolicy.Should().BeSameAs(policy);
    descriptor.RecoveryHelper.Should().BeNull();
    var setInvalid = () => descriptor.SetRestartPolicy(new RestartPolicy { InitialBackoff = TimeSpan.FromSeconds(30) });
    setInvalid.Should().Throw<ArgumentOutOfRangeException>();
    descriptor.RestartPolicy.Should().BeSameAs(policy);
  }

  /// <summary>
  /// Verifies that an invalid <see cref="EngineDescriptor.RestartPolicy"/> throws when it is set, not when the engine is
  /// started, and that null (no recovery) is accepted.
  /// </summary>
  [Fact]
  public void EngineDescriptor_RestartPolicy_InvalidThrowsWhenSet()
  {
    var descriptor = new EngineDescriptor("Network", Factory);

    var before = descriptor.RestartPolicy;

    var set = () => descriptor.RestartPolicy = new RestartPolicy { InitialBackoff = TimeSpan.FromSeconds(30) };

    set.Should().Throw<ArgumentOutOfRangeException>().Which.ParamName.Should().Be(nameof(RestartPolicy.MaxBackoff));
    descriptor.RestartPolicy.Should().BeSameAs(before, "a rejected policy leaves the previous one");
    descriptor.RestartPolicy = null;
    LifecycleTasks(descriptor).Should().OnlyContain(t => t.RecoveryHelper == null);
  }

  /// <summary>
  /// Verifies that an enforcer set directly is shared by the lifecycle tasks, as there is no telling how to create
  /// another, and that it wins over the restart policy, whichever was set first.
  /// </summary>
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void EngineDescriptor_SetRecoveryHelperDirectly_TheLifecycleTasksShareIt(bool policyFirst)
  {
    var enforcer = RestartPolicy.Default.GetEnforcer();
    var descriptor = new EngineDescriptor("Network", Factory);
    if (policyFirst)
      descriptor.SetRestartPolicy(RestartPolicy.Unlimited).SetRecoveryHelper(enforcer);
    else
      descriptor.SetRecoveryHelper(enforcer).SetRestartPolicy(RestartPolicy.Unlimited);

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
    coordinator.RegisterEngine("Network", Factory, requiredForRunning: true).SetRestartPolicy(RestartPolicy.Critical);

    var next = await coordinator
      .RestartWithDefinitionsAsync(coordinator.GetRegisteredDefinitions(), token: ct)
      .WaitAsync(TimeSpan.FromSeconds(5), ct);
    var copy = next.GetRegisteredDefinitions().Single();

    var helpers = LifecycleTasks(copy).Select(t => t.RecoveryHelper).ToArray();
    copy.RestartPolicy.Should().Be(RestartPolicy.Critical);
    helpers.Should().AllBeOfType<RestartPolicyEnforcer>();
    helpers.Distinct().Should().HaveCount(3);
  }
}
