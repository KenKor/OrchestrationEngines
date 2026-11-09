using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OrchestrationEngines.Tests;

/// <summary>
/// A clock that only moves when the test says so. Timers created on it (also those behind
/// <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>) fire when <see cref="Advance"/> passes their
/// due time.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
  private readonly object _lock = new();
  private readonly List<ManualTimer> _timers = [];
  private DateTimeOffset _now = start;

  /// <summary>
  /// Creates a clock that starts at a fixed moment.
  /// </summary>
  public ManualTimeProvider() : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
  {
  }

  /// <summary>
  /// Gets the number of timers that are waiting to fire.
  /// </summary>
  public int WaitingTimers
  {
    get { lock (_lock) { return _timers.Count(t => t.DueAt.HasValue); } }
  }

  /// <inheritdoc/>
  public override DateTimeOffset GetUtcNow()
  {
    lock (_lock) { return _now; }
  }

  /// <inheritdoc/>
  public override long GetTimestamp() => GetUtcNow().UtcTicks;

  /// <inheritdoc/>
  public override long TimestampFrequency => TimeSpan.TicksPerSecond;

  /// <summary>
  /// Moves the clock forward and fires every timer whose due time has passed, in due order.
  /// </summary>
  /// <param name="by">How far to move the clock.</param>
  public void Advance(TimeSpan by)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
    DateTimeOffset until;
    lock (_lock)
    {
      until = _now + by;
    }

    while (true)
    {
      ManualTimer due;
      lock (_lock)
      {
        due = _timers
          .Where(t => t.DueAt <= until)
          .OrderBy(t => t.DueAt)
          .FirstOrDefault();
        if (due == null)
        {
          _now = until;
          return;
        }
        _now = due.DueAt.Value;
        due.Schedule(due.Period == Timeout.InfiniteTimeSpan || due.Period == TimeSpan.Zero ? null : _now + due.Period);
      }

      // Fire outside the lock: the callback may create or change timers.
      due.Fire();
    }
  }

  /// <inheritdoc/>
  public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
  {
    var timer = new ManualTimer(this, callback, state);
    timer.Change(dueTime, period);
    lock (_lock)
    {
      _timers.Add(timer);
    }
    return timer;
  }

  private void Remove(ManualTimer timer)
  {
    lock (_lock)
    {
      _timers.Remove(timer);
    }
  }

  /// <summary>
  /// A timer on a <see cref="ManualTimeProvider"/>.
  /// </summary>
  private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object state) : ITimer
  {
    public DateTimeOffset? DueAt { get; private set; }

    public TimeSpan Period { get; private set; }

    public void Schedule(DateTimeOffset? dueAt) => DueAt = dueAt;

    public void Fire() => callback(state);

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
      lock (clock._lock)
      {
        Period = period;
        DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
      }
      return true;
    }

    public void Dispose() => clock.Remove(this);

    public ValueTask DisposeAsync()
    {
      Dispose();
      return ValueTask.CompletedTask;
    }
  }
}
