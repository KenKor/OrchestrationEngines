using System;
using System.Linq;
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
  // Without a dispatcher (the default here) the monitor loop never runs, so nothing takes the pulses the calls leave,
  // and the work runs on the calling thread. With one, the monitor loop runs once the coordinator is started.
  private sealed class PulsingCoordinator(bool useDispatcher = false)
    : EngineCoordinator(nameof(MonitorPulseTest), NullLogger.Instance, useDispatcher)
  {
    public Task CallAsync(Func<Task> work = null)
      => DispatchAsync(work ?? (() => Task.CompletedTask), TestContext.Current.CancellationToken);

    // Through the generic overload, which pulses on its own.
    public Task<T> CallWithResultAsync<T>(Func<Task<T>> work)
      => DispatchAsync(work, TestContext.Current.CancellationToken);
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

  /// <summary>
  /// Verifies that calls made at the same time also leave one wake-up between them.
  /// </summary>
  [Fact]
  public async Task MonitorPulse_ConcurrentCalls_LeaveOneWakeUp()
  {
    using var coordinator = new PulsingCoordinator();
    var token = TestContext.Current.CancellationToken;

    await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => coordinator.CallAsync(), token)));

    coordinator.PendingMonitorPulses.Should().Be(1);
  }

  /// <summary>
  /// Verifies that the parked monitor takes the wake-up of each call, the second as well as the first: a wake-up it
  /// took but left marked pending would stop every later call from waking it.
  /// </summary>
  [Fact]
  public async Task MonitorPulse_ParkedMonitor_TakesEachWakeUp()
  {
    using var coordinator = new PulsingCoordinator(useDispatcher: true);
    await coordinator.StartAsync();

    for (var call = 1; call <= 2; call++)
    {
      await coordinator.CallAsync();
      await Wait.UntilAsync(() => coordinator.PendingMonitorPulses == 0, MustComplete,
        $"the parked monitor must take the wake-up of call {call}");
    }
  }

  /// <summary>
  /// Verifies that a call wakes the monitor only once its work is handed over (run here, queued on a dispatcher): the
  /// tick the wake-up starts must come after that work, or the monitor can park again before the work resets tasks
  /// for it to start, and only one wake-up is held for all the calls.
  /// </summary>
  [Fact]
  public async Task MonitorPulse_ComesAfterTheWorkIsHandedOver()
  {
    using var coordinator = new PulsingCoordinator();
    var pendingDuringTheWork = -1;

    await coordinator.CallAsync(() =>
    {
      pendingDuringTheWork = coordinator.PendingMonitorPulses;
      return Task.CompletedTask;
    });

    pendingDuringTheWork.Should().Be(0);
    coordinator.PendingMonitorPulses.Should().Be(1);
  }

  /// <summary>
  /// Verifies that a call through the generic overload also wakes the monitor only once its work is handed over.
  /// </summary>
  [Fact]
  public async Task MonitorPulse_WithResult_ComesAfterTheWorkIsHandedOver()
  {
    using var coordinator = new PulsingCoordinator();

    var pendingDuringTheWork = await coordinator.CallWithResultAsync(() =>
      Task.FromResult(coordinator.PendingMonitorPulses));

    pendingDuringTheWork.Should().Be(0);
    coordinator.PendingMonitorPulses.Should().Be(1);
  }
}
