
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace OrchestrationEngines.Helpers;

/// <summary>
/// Provides helper members for task extensions.
/// </summary>
internal static class TaskExtensions
{
  /// <summary>
  /// Waits until <paramref name="task"/> has ended or <paramref name="after"/> has passed on <paramref name="clock"/>,
  /// whichever comes first, and removes the timer. Does not observe the task's failure.
  /// </summary>
  /// <param name="task">The task to wait for.</param>
  /// <param name="after">How long to wait at most.</param>
  /// <param name="clock">Measures <paramref name="after"/>.</param>
  /// <returns>Whether <paramref name="task"/> ended in time.</returns>
  public static async Task<bool> EndsWithinAsync(this Task task, TimeSpan after, TimeProvider clock)
  {
    if (task.IsCompleted)
      return true;
    using var cancelTimer = new CancellationTokenSource();
    var timer = Task.Delay(after, clock, cancelTimer.Token);
    var first = await Task.WhenAny(task, timer).ConfigureAwait(false);
    cancelTimer.Cancel();
    return first == task;
  }

  /// <summary>
  /// Waits for <paramref name="task"/> until <paramref name="token"/> is cancelled. When the wait is cancelled first, a
  /// failure of the task that comes later is still read, so it is not reported as an unobserved task exception (#86
  /// item 6); the task keeps at most one such observer, however many waits on it are cancelled. Named apart from the
  /// framework's <c>Task.WaitAsync</c>, which on newer targets would be called instead.
  /// </summary>
  /// <param name="task">The task to wait for.</param>
  /// <param name="token">Ends the wait, not the task.</param>
  /// <returns>The task's own outcome, or a cancellation when <paramref name="token"/> is cancelled first.</returns>
  public static Task WaitObservedAsync(this Task task, CancellationToken token)
  {
    ArgumentNullException.ThrowIfNull(task);
    // A wait that cannot be cancelled first is the task itself.
    return task.IsCompleted || !token.CanBeCanceled ? task : WaitThenObserveAsync(task, token);
  }

  /// <inheritdoc cref="WaitObservedAsync(Task, CancellationToken)"/>
  public static Task<T> WaitObservedAsync<T>(this Task<T> task, CancellationToken token)
  {
    ArgumentNullException.ThrowIfNull(task);
    return task.IsCompleted || !token.CanBeCanceled ? task : WaitThenResultAsync(task, token);
  }

  private static async Task<T> WaitThenResultAsync<T>(Task<T> task, CancellationToken token)
  {
    await WaitThenObserveAsync(task, token).ConfigureAwait(false);
    // The task has ended: its result, or its own failure.
    return await task.ConfigureAwait(false);
  }

  private static async Task WaitThenObserveAsync(Task task, CancellationToken token)
  {
    try
    {
      await WaitCoreAsync(task, token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested)
    {
      ObserveFailure(task);
      throw;
    }
  }

  /// <summary>
  /// The tasks that have an observer from <see cref="ObserveFailure"/>; a task leaves when it is collected.
  /// </summary>
  private static readonly ConditionalWeakTable<Task, object> s_observed = new();

  /// <summary>
  /// Reads the exception of <paramref name="task"/> if it fails, now or later. A pending task gets one observer only:
  /// a latch that many cancelled waits leave behind does not collect one per wait.
  /// </summary>
  /// <returns>Whether an observer was attached; not when the task has ended or already has one.</returns>
  internal static bool ObserveFailure(Task task)
  {
    if (task.IsCompleted)
    {
      _ = task.Exception;
      return false;
    }
    lock (s_observed)
    {
      if (s_observed.TryGetValue(task, out _))
        return false;
      s_observed.Add(task, null);
    }
    task.ContinueWith(
      static t => _ = t.Exception,
      CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default);
    return true;
  }

#if NET6_0_OR_GREATER
  private static Task WaitCoreAsync(Task task, CancellationToken token) => task.WaitAsync(token);
#else
  private static async Task WaitCoreAsync(Task task, CancellationToken token)
  {
    // The token can be cancelled: a second task is set up that completes when it is cancelled.
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var reg = token.UnsafeRegister(_ => tcs.TrySetCanceled(token), null);
    var completed = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
    await completed.ConfigureAwait(false);
  }
#endif
}
