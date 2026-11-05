using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrchestrationEngines.Coordinator;
using OrchestrationEngines.Engine;
using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Mechanic;

/// <summary>
/// EngineTask responsible for computing and assigning context to an engine instance.
/// </summary>
/// <remarks>
/// <para>
/// This task represents the second lifecycle step: deriving the runtime context
/// required by an engine before it can be started.
/// </para>
///
/// <para>
/// Context computation may depend on:
/// <list type="bullet">
/// <item>external resources,</item>
/// <item>configuration state,</item>
/// <item>other engines being present and ready.</item>
/// </list>
/// These dependencies are expressed through EngineTask dependencies rather than
/// through direct engine calls.
/// </para>
///
/// <para>
/// The computed context is cached in <see cref="ContextComputed"/> for diagnostics
/// and for potential reuse during recovery or invalidation flows.
/// </para>
/// </remarks>
public class EngineContextTask : EngineTask
{
  /// <summary>
  /// Postfix appended to the engine name to form the task name for context computation.
  /// </summary>
  public const string PostFix = "{oo}";

  /// <summary>
  /// Descriptor defining how context is computed for the engine.
  /// </summary>
  public EngineDescriptor Descriptor { get; }

  /// <summary>
  /// Creates a task that computes and registers context for an engine.
  /// </summary>
  public EngineContextTask(
      MechanicEngine m,
      EngineDescriptor engineDescriptor,
      ILogger Logger,
      CancellationToken token)
      : base(
          engineDescriptor.Name + PostFix,
          async _ => await Task.CompletedTask.ConfigureAwait(false), // Placeholder; replaced immediately.
          engineDescriptor.RequiredForRunning,
          Logger,
          token)
  {
    Descriptor = engineDescriptor;

    // Context computation recovery currently uses the same policy as the other lifecycle steps, with an enforcer of its
    // own. This may be refined later to distinguish context failures from launch failures.
    RecoveryHelper = engineDescriptor.CreateRecoveryHelper(TimeProvider);

    _workAsync = runtimeToken => CreateContextAsync(m, engineDescriptor, runtimeToken);
  }

  /// <summary>
  /// Computes context and registers it with the mechanic.
  /// </summary>
  /// <param name="m">The m.</param>
  /// <param name="engineDescriptor">The engine descriptor.</param>
  /// <param name="token">The token.</param>
  protected async Task CreateContextAsync(
      MechanicEngine m,
      EngineDescriptor engineDescriptor,
      CancellationToken token)
  {
    // Note that this will intentionally throw if not context has been computed, so it will be scheduled for recomputing till it succeeds or is abandoned.
    ContextComputed = await engineDescriptor.ContextFactory(token).ConfigureAwait(false) ?? throw new InvalidEngineContextException(engineDescriptor.Name);
    m.RegisterContext(engineDescriptor.Name, ContextComputed);
  }

  /// <summary>
  /// The most recent context value computed for this engine.
  /// </summary>
  /// <remarks>
  /// This is primarily for diagnostics and invalidation handling.
  /// A reset of this task will clear this value.
  /// </remarks>
  public IEngineContext ContextComputed { get; set; }
}
