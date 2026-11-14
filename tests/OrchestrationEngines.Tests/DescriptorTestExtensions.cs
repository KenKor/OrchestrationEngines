using OrchestrationEngines.Coordinator;

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
