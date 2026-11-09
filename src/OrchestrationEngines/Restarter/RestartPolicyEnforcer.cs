using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OrchestrationEngines.Tasks;

namespace OrchestrationEngines.Restarter;

/// <summary>
/// Default implementation of <see cref="IRestartPolicyEnforcer"/> that applies a
/// bounded restart rate and exponential backoff between recovery attempts.
/// </summary>
/// <remarks>
/// <para>
/// This enforcer is intended to protect the host process from �flapping� tasks that fail
/// repeatedly in a short time span. It does so by:
/// </para>
/// <list type="bullet">
/// <item>
/// Enforcing an optional maximum number of restarts within an optional time window
/// (<see cref="RestartPolicy.MaxRestartsInWindow"/> and <see cref="RestartPolicy.Window"/>).
/// </item>
/// <item>
/// Applying an exponential backoff delay between recovery attempts, clamped to a maximum
/// (<see cref="RestartPolicy.InitialBackoff"/> and <see cref="RestartPolicy.MaxBackoff"/>).
/// </item>
/// </list>
/// <para>
/// The enforcer maintains per-instance state (restart timestamps and current backoff), and therefore
/// is expected to be scoped to a single <see cref="EngineTask"/>.
/// </para>
/// </remarks>
public class RestartPolicyEnforcer : IRestartPolicyEnforcer
{
  private readonly RestartPolicy _policy;

  /// <summary>
  /// The clock that dates the restarts and times the backoff delay.
  /// </summary>
  private readonly TimeProvider _timeProvider;

  /// <summary>
  /// Gets the timestamps (UTC) of recorded restart attempts used to enforce the restart-rate limit.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This collection is mutated under a lock on the list instance.
  /// </para>
  /// <para>
  /// When a restart window is configured, old timestamps are pruned to maintain a sliding-window view.
  /// </para>
  /// </remarks>
  public List<DateTimeOffset> RestartTimestamps { get; } = new List<DateTimeOffset>();

  /// <summary>
  /// Gets the number of restarts since the last successful run or reset (counted under the lock on
  /// <see cref="RestartTimestamps"/>).
  /// </summary>
  public int ConsecutiveRestarts { get; private set; }

  /// <summary>
  /// Gets or sets the backoff delay that will be applied on the next recovery attempt.
  /// </summary>
  /// <remarks>
  /// The backoff is updated after each recovery invocation using an exponential progression
  /// (doubling) and clamped to the configured maximum.
  /// </remarks>
  public TimeSpan CurrentBackoff { get; set; }

  /// <summary>
  /// Initializes a new instance of the class.
  /// </summary>
  /// <param name="policy">The restart policy configuration to enforce.</param>
  /// <param name="timeProvider">
  /// The clock that dates the restarts and times the backoff delay; <see cref="TimeProvider.System"/> when <c>null</c>.
  /// Tests pass a clock they advance themselves.
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="policy"/> is <c>null</c>.</exception>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when the policy contains invalid values (negative limits or backoff settings, or a max backoff
  /// smaller than the initial backoff).
  /// </exception>
  /// <remarks>
  /// <para>
  /// Validation rules:
  /// </para>
  /// <list type="bullet">
  /// <item><c>MaxRestartsInWindow</c> may be 0 (disables rate limiting) but must not be negative.</item>
  /// <item><c>Window</c> may be 0 (disables windowing) but must not be negative.</item>
  /// <item><c>InitialBackoff</c> must not be negative.</item>
  /// <item><c>MaxBackoff</c> must be greater than or equal to <c>InitialBackoff</c>.</item>
  /// </list>
  /// </remarks>
  public RestartPolicyEnforcer(RestartPolicy policy, TimeProvider timeProvider = null)
  {
    _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    _timeProvider = timeProvider ?? TimeProvider.System;
    _policy.Validate();
    CurrentBackoff = _policy.InitialBackoff;
  }

  /// <summary>
  /// Resets the enforcer�s internal state to its initial configuration.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This clears all recorded restart timestamps and resets <see cref="CurrentBackoff"/> to the policy�s
  /// initial backoff. It should be called when the associated task transitions into a clean start/reset
  /// state so that historical failures do not penalize subsequent independent runs.
  /// </para>
  /// </remarks>
  public void Reset()
  {
    lock (RestartTimestamps)
    {
      RestartTimestamps.Clear();
      ConsecutiveRestarts = 0;
    }

    CurrentBackoff = _policy.InitialBackoff;
  }

  /// <summary>
  /// Enforces restart limits and applies an exponential backoff delay before allowing a restart.
  /// </summary>
  /// <param name="et">The engine task currently undergoing recovery.</param>
  /// <param name="token">Cancellation token controlling the recovery operation.</param>
  /// <returns>A task that completes after any enforced delay has elapsed.</returns>
  /// <exception cref="TaskCanceledException">
  /// Thrown when invoked outside the expected recovery phase or when cancellation is requested.
  /// </exception>
  /// <exception cref="EngineTaskLimitExceededException">
  /// Thrown when the task exceeds <see cref="RestartPolicy.MaxRestartsInWindow"/> within the configured
  /// <see cref="RestartPolicy.Window"/>.
  /// </exception>
  /// <remarks>
  /// <para>
  /// This method is expected to be called only while the task is in the <see cref="EngineTaskState.Recovering"/>
  /// state. Calling it in any other state indicates a wiring/usage error and is treated as cancellation.
  /// </para>
  /// <para>
  /// If a restart limit is configured (<c>MaxRestartsInWindow &gt; 0</c>), the current timestamp is recorded,
  /// older timestamps may be pruned based on the configured window, and the rate limit is enforced.
  /// </para>
  /// <para>
  /// The delay is computed using the current backoff and then the backoff is updated (doubling) for the
  /// next invocation. The delay itself is applied outside the internal lock to avoid blocking other operations.
  /// </para>
  /// </remarks>
  public async Task Invoke(EngineTask et, CancellationToken token)
  {
    // Because to the wiring, we should only be called from
    if (et.State != EngineTaskState.Recovering) throw new TaskCanceledException(nameof(et));

    var delayToApply = CurrentBackoff;

    lock (RestartTimestamps)
    {
      // Check if we check on max nr of restarts.
      if (_policy.MaxRestartsInWindow > 0)
      {
        var now = _timeProvider.GetUtcNow();
        // Check if we check on window.
        if (_policy.Window > TimeSpan.Zero)
        {
          // Enforce the sliding time window.
          RestartTimestamps.RemoveAll(ts => now - ts > _policy.Window);
        }

        // Record the failure timestamp.
        RestartTimestamps.Add(now);

        // Check if the retries are within the window.
        if (RestartTimestamps.Count > _policy.MaxRestartsInWindow)
        {
          throw new EngineTaskLimitExceededException(et, _policy.MaxRestartsInWindow, _policy.Window);
        }
      }

      // Check the restarts in a row: the window alone never gives up on an attempt that takes longer to fail than
      // Window / MaxRestartsInWindow, since older restarts leave the window before the limit is reached.
      ConsecutiveRestarts++;
      if (_policy.MaxConsecutiveRestarts > 0 && ConsecutiveRestarts > _policy.MaxConsecutiveRestarts)
      {
        throw EngineTaskLimitExceededException.ForConsecutiveRestarts(et, _policy.MaxConsecutiveRestarts);
      }

      // Exponential backoff update for next time
      CurrentBackoff = NextBackoff(delayToApply, _policy.MaxBackoff);
    }

    // Check if this task was canceled in the mean time.
    if (token.IsCancellationRequested) throw new TaskCanceledException();

    // Apply delay outside lock (important). With the recovery's own token: the task's token may already be the next
    // run's, after a reset during this recovery, and that one is not cancelled when this recovery is.
    if (delayToApply > TimeSpan.Zero)
      await Task.Delay(delayToApply, _timeProvider, token).ConfigureAwait(false);
  }

  /// <summary>
  /// Computes the next exponential backoff value (doubling) and clamps it to the specified maximum.
  /// </summary>
  /// <param name="current">Current backoff value.</param>
  /// <param name="max">Maximum allowed backoff.</param>
  /// <returns>The next backoff value, clamped to <paramref name="max"/>.</returns>
  /// <remarks>
  /// Returns <see cref="TimeSpan.Zero"/> when <paramref name="current"/> is non-positive.
  /// </remarks>
  private static TimeSpan NextBackoff(TimeSpan current, TimeSpan max)
  {
    if (current <= TimeSpan.Zero) return TimeSpan.Zero;

    // Multiply by 2 with overflow-safe clamp
    var nextMs = current.TotalMilliseconds * 2.0;
    var maxMs = max.TotalMilliseconds;

    if (nextMs > maxMs) nextMs = maxMs;
    if (nextMs < 0) nextMs = maxMs; // defensive

    return TimeSpan.FromMilliseconds(nextMs);
  }
}
