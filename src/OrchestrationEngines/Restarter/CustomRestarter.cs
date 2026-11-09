using System;
using System.Threading;
using System.Threading.Tasks;
using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Restarter
{
  /// <summary>
  /// Restart policy enforcer that delegates recovery behavior to a custom asynchronous function.
  /// </summary>
  /// <remarks>
  /// <para>
  /// <see cref="CustomRestarter"/> allows callers to inject bespoke recovery logic into the
  /// <see cref="EngineTask"/> restart pipeline without creating a dedicated policy type.
  /// </para>
  /// <para>
  /// It acts as a thin adapter between the <see cref="IRestartPolicyEnforcer"/> interface and
  /// a caller-provided asynchronous recovery function, making it suitable for scenarios where
  /// restart behavior must be composed dynamically or configured at runtime.
  /// </para>
  /// </remarks>
  public class CustomRestarter : IRestartPolicyEnforcer
  {
    private readonly Func<EngineTask, CancellationToken, Task> _recoveryHelper;

    /// <summary>
    /// Initializes a new instance of the class.
    /// </summary>
    /// <param name="recoveryAsync">
    /// Asynchronous function that performs recovery for a failed <see cref="EngineTask"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="recoveryAsync"/> is <c>null</c>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The supplied function is invoked whenever the associated <see cref="EngineTask"/>
    /// enters a restart or recovery phase, subject to the surrounding restart policy.
    /// </para>
    /// <para>
    /// The function is responsible for honoring the provided <see cref="CancellationToken"/>
    /// and for throwing if recovery fails irrecoverably.
    /// </para>
    /// </remarks>
    public CustomRestarter(Func<EngineTask, CancellationToken, Task> recoveryAsync)
    {
      _recoveryHelper = recoveryAsync ?? throw new ArgumentNullException(nameof(recoveryAsync));
    }

    /// <summary>
    /// Resets internal policy state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation maintains no internal state and therefore performs no action.
    /// </para>
    /// <para>
    /// The method exists to satisfy the <see cref="IRestartPolicyEnforcer"/> contract and
    /// to allow uniform handling alongside stateful policy implementations.
    /// </para>
    /// </remarks>
    public void Reset()
    {
    }

    /// <summary>
    /// Invokes the custom recovery logic for the specified engine task.
    /// </summary>
    /// <param name="et">The engine task requiring recovery.</param>
    /// <param name="token">Cancellation token controlling the recovery operation.</param>
    /// <returns>
    /// A task that completes when recovery has finished.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method directly delegates to the recovery function provided at construction time.
    /// </para>
    /// <para>
    /// Any exception thrown by the recovery function is propagated to the caller and interpreted
    /// by the surrounding restart infrastructure.
    /// </para>
    /// </remarks>
    public Task Invoke(EngineTask et, CancellationToken token)
        => _recoveryHelper(et, token);
  }

}
