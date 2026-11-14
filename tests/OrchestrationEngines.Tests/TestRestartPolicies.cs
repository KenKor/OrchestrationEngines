using System;
using OrchestrationEngines.Restarter;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Restart policies without a wait, shared by the tests about recovery.
/// </summary>
internal static class TestRestartPolicies
{
  /// <summary>
  /// A policy that allows <paramref name="maxRestarts"/> restarts in a 10-minute window, without a wait.
  /// </summary>
  public static RestartPolicy NoWait(int maxRestarts) => new()
  {
    MaxRestartsInWindow = maxRestarts,
    Window = TimeSpan.FromMinutes(10),
    InitialBackoff = TimeSpan.Zero,
    MaxBackoff = TimeSpan.Zero,
  };

  /// <summary>
  /// A policy that allows <paramref name="maxRestarts"/> restarts in a row, without a window or a wait.
  /// </summary>
  public static RestartPolicy NoWaitInARow(int maxRestarts) => new()
  {
    MaxConsecutiveRestarts = maxRestarts,
    InitialBackoff = TimeSpan.Zero,
    MaxBackoff = TimeSpan.Zero,
  };
}
