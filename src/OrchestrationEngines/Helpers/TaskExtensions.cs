
using System;
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

#if !NET6_0_OR_GREATER
  /// <summary>
  /// Executes the wait async operation.
  /// </summary>
  /// <param name="task">The task.</param>
  /// <param name="token">The token.</param>
  /// <returns>The result.</returns>
  public static async Task WaitAsync(this Task task, CancellationToken token)
  {
    if (task == null) throw new ArgumentNullException(nameof(task));
    if (!token.CanBeCanceled)
    {
      await task.ConfigureAwait(false);
      return;
    }
    // If the token can be cancelled, a second task is setup that completes when it is cancelled.
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var reg = token.Register(() => tcs.TrySetCanceled(token));
    var completed = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
    await completed.ConfigureAwait(false);
  }

  public static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken token)
  {
    if (task == null) throw new ArgumentNullException(nameof(task));
    if (!token.CanBeCanceled)
      return await task.ConfigureAwait(false);
    // If the token can be cancelled, a second task is setup that completes when it is cancelled.
    var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var reg = token.Register(() => tcs.TrySetCanceled(token));
    var completed = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
    return await completed.ConfigureAwait(false);
  }
#else
  // On modern frameworks, rely on the BCL implementations.
  /// <inheritdoc/>
  public static Task WaitAsync(this Task task, CancellationToken token) => task.WaitAsync(token);
  public static Task<T> WaitAsync<T>(this Task<T> task, CancellationToken token) => task.WaitAsync(token);
#endif
}
