using System;
using System.Threading;
using System.Threading.Tasks;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Descriptor settings shared by the tests.
/// </summary>
internal static class DescriptorTestExtensions
{
  /// <summary>
  /// An engine factory that creates no engine: for descriptors that are only compared, never started.
  /// </summary>
  public static readonly Func<CancellationToken, Task<IEngine>> NullEngineFactory = _ => Task.FromResult<IEngine>(null);

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
