using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OrchestrationEngines.Coordinator;

namespace OrchestrationEngines.Tests;

/// <summary>
/// A generation switch followed by a wait until the next generation is ready, as most tests want it.
/// </summary>
internal static class SwitchUntilReady
{
  /// <summary>
  /// Switches to a generation built from <paramref name="candidates"/>, then waits until it is ready.
  /// </summary>
  public static async Task<EngineCoordinator> SwitchUntilReadyAsync(this EngineCoordinator coordinator,
    IEnumerable<EngineDescriptor> candidates, CancellationToken token = default)
  {
    var next = await coordinator.RestartWithDefinitionsAsync(candidates).ConfigureAwait(false);
    await next.EnsureReadyAsync(token).ConfigureAwait(false);
    return next;
  }
}
