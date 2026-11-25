using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Mechanic;
using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Tests;

/// <summary>
/// A coordinator that resets an engine's context task, and every task that depends on it, through its mechanic: the
/// context is computed again and the engine and its dependents are restarted in dependency order. No public call does
/// this since the context invalidation was removed (#226), but the kick-start's restart code it reaches stays (#232).
/// It also shows the mechanic's tasks, to wait for one.
/// </summary>
internal sealed class ContextResettingCoordinator(string name) : EngineCoordinator(name, NullLogger.Instance)
{
  /// <summary>Resets the context task of <paramref name="engineName"/> and the tasks that depend on it.</summary>
  public Task ResetContextAsync(string engineName) =>
    Mechanic.ResetTasksAsync([EngineContextTask.ContextName(engineName)], resetDependentTasks: true);

  /// <summary>The kick-start task of <paramref name="engineName"/>, to wait until a run of it has ended.</summary>
  public EngineTask KickStartOf(string engineName) => Mechanic.Tasks[EngineKickStartTask.KickStartName(engineName)];

  /// <summary>
  /// Waits until the mechanic's task <paramref name="taskName"/> has completed with success, such as an engine's
  /// creation task, which bears the engine's name.
  /// </summary>
  public Task EnsureMechanicTaskReadyAsync(string taskName, CancellationToken token) =>
    Mechanic.EnsureTaskReadyAsync(taskName, token);
}
