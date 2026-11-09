using System.Threading;
using System.Threading.Tasks;
using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Restarter;

/// <summary>
/// Defines a contract for enforcing restart and recovery behavior for an <see cref="EngineTask"/>.
/// </summary>
/// <remarks>
/// <para>
/// Implementations of <see cref="IRestartPolicyEnforcer"/> control how and when an
/// <see cref="EngineTask"/> is recovered after failure. This includes decisions such as
/// whether recovery is attempted, how often it may occur, and when failure is considered
/// terminal.
/// </para>
/// <para>
/// The interface intentionally separates policy enforcement from task execution logic,
/// allowing restart strategies to be swapped, composed, or configured dynamically.
/// </para>
/// </remarks>
public interface IRestartPolicyEnforcer
{
  /// <summary>
  /// Resets any internal state maintained by the restart policy.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This method is invoked when the associated <see cref="EngineTask"/> transitions
  /// into a clean start or reset state.
  /// </para>
  /// <para>
  /// Stateless implementations may provide an empty implementation.
  /// </para>
  /// </remarks>
  void Reset();

  /// <summary>
  /// Invokes recovery logic for a failed <see cref="EngineTask"/>.
  /// </summary>
  /// <param name="et">The engine task requiring recovery.</param>
  /// <param name="token">
  /// Cancellation token controlling the recovery operation. Implementations must
  /// observe this token and abort promptly when cancellation is requested.
  /// </param>
  /// <returns>
  /// A task that completes when recovery has finished.
  /// </returns>
  /// <remarks>
  /// <para>
  /// If recovery succeeds, the task may be restarted according to the surrounding
  /// restart infrastructure.
  /// </para>
  /// <para>
  /// If recovery fails or a restart limit is exceeded, implementations should throw
  /// an exception (e.g., <see cref="EngineTaskLimitExceededException"/>) to signal
  /// a terminal failure condition.
  /// </para>
  /// </remarks>
  Task Invoke(EngineTask et, CancellationToken token);
}