using System;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Restarter;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Descriptor settings shared by the tests.
/// </summary>
internal static class DescriptorTestExtensions
{
  /// <summary>
  /// Turns off the descriptor's default <see cref="EngineDescriptor.RestartPolicy"/>, so a failed load faults at once
  /// instead of retrying with backoff: for tests about what happens once an engine has faulted.
  /// </summary>
  public static EngineDescriptor WithoutRestarts(this EngineDescriptor descriptor)
  {
    descriptor.RestartPolicy = null;
    return descriptor;
  }
}

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
