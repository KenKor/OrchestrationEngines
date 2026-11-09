using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Mechanic;

/// <summary>
/// EngineTask responsible for creating a runtime engine instance.
/// </summary>
/// <remarks>
/// <para>
/// This task represents the first lifecycle step for an engine: instantiation.
/// It wraps the engine factory (<see cref="EngineDescriptor.EngineFactory"/>) and
/// registers the resulting engine instance with the <see cref="MechanicEngine"/>.
/// </para>
///
/// <para>
/// Creation is separated from context assignment and kick-starting so that:
/// <list type="bullet">
/// <item>engine instances exist before contexts are computed,</item>
/// <item>dependencies can be expressed cleanly between lifecycle phases,</item>
/// <item>recovery policies can be refined independently per phase.</item>
/// </list>
/// </para>
///
/// <para>
/// Note: Recovery for engine creation currently reuses the engine's recovery (see
/// <see cref="EngineDescriptor.CreateRecoveryHelper"/>), but this is conceptually different from launch/restart
/// recovery and may warrant a separate policy.
/// </para>
/// </remarks>
public class EngineCreationTask : EngineTask
{
    /// <summary>
    /// Descriptor defining how this engine is created and configured.
    /// </summary>
    public EngineDescriptor Descriptor { get; }

    /// <summary>
    /// Creates a task that instantiates an engine and registers it with the mechanic.
    /// </summary>
    public EngineCreationTask(
        MechanicEngine m,
        EngineDescriptor engineDescriptor,
        ILogger Logger,
        CancellationToken token)
        : base(
            engineDescriptor.Name,
            async _ => await Task.CompletedTask.ConfigureAwait(false), // Placeholder; replaced immediately.
            engineDescriptor.RequiredForRunning,
            Logger,
            token)
    {
        Descriptor = engineDescriptor;

        // Creation-time recovery is not the same as runtime restart recovery.
        // This currently reuses the descriptor policy as a pragmatic default, with an enforcer of its own.
        RecoveryHelper = engineDescriptor.CreateRecoveryHelper(TimeProvider);

        // Override the placeholder work delegate with the actual creation logic.
        _workAsync = runtimeToken => CreateInstanceAsync(m, engineDescriptor, runtimeToken);
    }

    /// <summary>
    /// Invokes the engine factory and registers the created engine instance.
    /// </summary>
    /// <param name="m">The m.</param>
    /// <param name="engineDescriptor">The engine descriptor.</param>
    /// <param name="token">The token.</param>
    protected static async Task CreateInstanceAsync(
        MechanicEngine m,
        EngineDescriptor engineDescriptor,
        CancellationToken token)
    {
        var engineInstance = await engineDescriptor.EngineFactory(token).ConfigureAwait(false);
        m.RegisterEngine(engineDescriptor.Name, engineInstance);
    }
}
