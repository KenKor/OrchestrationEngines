using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Tasks;
using Xunit;
using static OrchestrationEngines.Tests.TestRestartPolicies;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for a recovery that finds no <see cref="EngineTask.RecoveryHelper"/>: the property is public and can
/// be cleared after the task entered Recovering.
/// </summary>
public class RecoveryWithoutHelperTest
{
  /// <summary>
  /// A task whose first run fails and whose recovery helper is cleared when the recovery starts.
  /// </summary>
  private sealed class HelperClearedTask() : EngineTask("Load", _ => throw new InvalidOperationException("Run fails."),
    requiredForRunning: true, NullLogger.Instance, CancellationToken.None)
  {
    protected override Task ExecuteRecoveryAsync(int run)
    {
      RecoveryHelper = null;
      return base.ExecuteRecoveryAsync(run);
    }
  }

  /// <summary>
  /// Verifies that a recovery without a recovery helper ends the task in RecoveryFailed, with the reason, and does not
  /// leave it in Recovering.
  /// </summary>
  [Fact]
  public async Task EngineTask_RecoveryWithoutHelper_EndsInRecoveryFailed()
  {
    var task = new HelperClearedTask();
    task.SetRecovery(NoWait(maxRestarts: 1));

    await task.FireAsync(EngineTaskTrigger.Start);
    await task.InnerTask.WaitAsync(MustComplete, TestContext.Current.CancellationToken);

    task.State.Should().Be(EngineTaskState.RecoveryFailed);
    task.FailReason.Should().Contain(nameof(EngineTask.RecoveryHelper));
  }
}
