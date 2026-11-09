using System;

namespace OrchestrationEngines.Restarter;

/// <summary>
/// Settings that form a Restart Policy
/// </summary>
public sealed class RestartPolicy : IEquatable<RestartPolicy>
{
  /// <summary>
  /// Max nr of restarts allowed within a time window before failing.
  /// Set this to 0 to stop checking failures within a window.
  /// </summary>
  public int MaxRestartsInWindow { get; set; } = 5;

  /// <summary>
  /// Restart Window
  /// Set this to TimeSpan.Zero to stop checking failures within a window.
  /// </summary>
  public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(3);

  /// <summary>
  /// Max nr of restarts in a row without a successful run before failing, however long each attempt takes; a success
  /// (or a reset) starts counting again. The window alone never gives up on an attempt that takes longer than
  /// Window / MaxRestartsInWindow to fail: older restarts leave the window before the limit is reached.
  /// Set this to 0 (the default for a policy you create) to stop checking restarts in a row.
  /// </summary>
  public int MaxConsecutiveRestarts { get; set; }

  /// <summary>
  /// Initial Backoff time
  /// </summary>
  public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Maximum Backoff time
  /// </summary>
  public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(10);

  /// <summary>
  /// Default restart policy. For typical engine, IO-bound / flaky:<br/>
  /// MaxRestarts: 5<br/>
  /// Window: 3 minute<br/>
  /// MaxConsecutiveRestarts: 5<br/>
  /// Initial backoff: 1 (s)<br/>
  /// MaxBackoff: 10 (s)
  /// </summary>
  public static RestartPolicy Default => new() { MaxConsecutiveRestarts = 5 };

  /// <summary>
  /// Critical restart policy. For typical engine, Critical Infrastructure:<br/>
  /// MaxRestarts: 3<br/>
  /// Window: 1 minute<br/>
  /// MaxConsecutiveRestarts: 3<br/>
  /// Initial backoff: 500 (ms)<br/>
  /// MaxBackoff: 30 (s) 
  /// </summary>
  public static RestartPolicy Critical => new()
  {
    MaxRestartsInWindow = 3,
    Window = TimeSpan.FromMinutes(1),
    MaxConsecutiveRestarts = 3,
    InitialBackoff = TimeSpan.FromMilliseconds(500),
    MaxBackoff = TimeSpan.FromSeconds(30)
  };

  /// <summary>
  /// External restart policy. For typical engine, External Dependency:<br/>
  /// MaxRestarts: 10<br/>
  /// Window: 10 minute<br/>
  /// MaxConsecutiveRestarts: 10<br/>
  /// Initial backoff: 2 (s)<br/>
  /// MaxBackoff: 10 (s) 
  /// </summary>
  public static RestartPolicy External => new()
  {
    MaxRestartsInWindow = 10,
    Window = TimeSpan.FromMinutes(10),
    MaxConsecutiveRestarts = 10,
    InitialBackoff = TimeSpan.FromSeconds(2),
    MaxBackoff = TimeSpan.FromSeconds(10),
  };

  /// <summary>
  /// Unlimited restart policy. For sine-qua-non engine, Optional / Best-effort:<br/>
  /// MaxRestarts: 0<br/>
  /// Window: TimeSpan.Zero<br/>
  /// MaxConsecutiveRestarts: 0<br/>
  /// Initial backoff: 1 (s)<br/>
  /// MaxBackoff: 60 (s) <br/>
  /// </summary>
  public static RestartPolicy Unlimited => new()
  {
    MaxRestartsInWindow = 0,
    Window = TimeSpan.Zero,
    InitialBackoff = TimeSpan.FromSeconds(1),
    MaxBackoff = TimeSpan.FromSeconds(60),
  };

  /// <summary>
  /// Get an enforcer for this restart policy.
  /// </summary>
  /// <param name="timeProvider">The clock the enforcer uses; <see cref="TimeProvider.System"/> when <c>null</c>.</param>
  /// <returns></returns>
  public IRestartPolicyEnforcer GetEnforcer(TimeProvider timeProvider = null) => new RestartPolicyEnforcer(this, timeProvider);

  /// <summary>
  /// Throws if the settings cannot be enforced (see <see cref="RestartPolicyEnforcer"/>'s constructor).
  /// </summary>
  /// <exception cref="ArgumentOutOfRangeException">A setting is negative, or <see cref="MaxBackoff"/> is less than
  /// <see cref="InitialBackoff"/>.</exception>
  internal void Validate()
  {
    // 0 means disable this check.
    if (MaxRestartsInWindow < 0) throw new ArgumentOutOfRangeException(nameof(MaxRestartsInWindow));
    if (Window < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Window));
    if (MaxConsecutiveRestarts < 0) throw new ArgumentOutOfRangeException(nameof(MaxConsecutiveRestarts));
    if (InitialBackoff < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(InitialBackoff));
    if (MaxBackoff < InitialBackoff) throw new ArgumentOutOfRangeException(nameof(MaxBackoff));
  }

  /*
   * Default IEquatable implementation
   */

  /// <inheritdoc/>
  public bool Equals(RestartPolicy other)
  {
    if (other is null) return false;
    if (ReferenceEquals(this, other)) return true;
    return MaxRestartsInWindow == other.MaxRestartsInWindow && Window.Equals(other.Window) &&
           MaxConsecutiveRestarts == other.MaxConsecutiveRestarts &&
           InitialBackoff.Equals(other.InitialBackoff) && MaxBackoff.Equals(other.MaxBackoff);
  }

  /// <inheritdoc/>
  public override bool Equals(object obj)
  {
    return ReferenceEquals(this, obj) || obj is RestartPolicy other && Equals(other);
  }

  /// <inheritdoc/>
  public override int GetHashCode()
  {
    return HashCode.Combine(MaxRestartsInWindow, Window, MaxConsecutiveRestarts, InitialBackoff, MaxBackoff);
  }
}