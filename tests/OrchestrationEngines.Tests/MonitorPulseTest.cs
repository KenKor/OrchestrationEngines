using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests of the signal that wakes the coordinator's monitor loop from its parked state.
/// </summary>
public class MonitorPulseTest
{
  // Without a dispatcher the monitor loop never runs, so nothing takes the pulses the calls leave.
  private sealed class PulsingCoordinator() : EngineCoordinator(nameof(MonitorPulseTest), NullLogger.Instance,
    useDispatcher: false)
  {
    public Task CallAsync() => DispatchAsync(() => Task.CompletedTask, TestContext.Current.CancellationToken);
  }

  /// <summary>
  /// Verifies that calls made while the monitor does not wait leave one wake-up between them, not one each: the
  /// parked monitor needs only one tick to see all the work they scheduled.
  /// </summary>
  [Fact]
  public async Task MonitorPulse_ManyCalls_LeaveOneWakeUp()
  {
    using var coordinator = new PulsingCoordinator();

    for (var i = 0; i < 3; i++)
      await coordinator.CallAsync();

    coordinator.PendingMonitorPulses.Should().Be(1);
  }
}
