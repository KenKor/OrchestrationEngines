
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

  /// <summary>
  /// Waits for <paramref name="task"/> until <paramref name="token"/> is cancelled. When the wait is cancelled first, a
  /// failure of the task that comes later is still read, so it is not reported as an unobserved task exception (#86
  /// item 6). Named apart from the framework's <c>Task.WaitAsync</c>, which on newer targets would be called instead.
  /// </summary>
  /// <param name="task">The task to wait for.</param>
  /// <param name="token">Ends the wait, not the task.</param>
  /// <returns>The task's own outcome, or a cancellation when <paramref name="token"/> is cancelled first.</returns>
  public static async Task WaitObservedAsync(this Task task, CancellationToken token)
  {
    if (task == null) throw new ArgumentNullException(nameof(task));
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

  /// <inheritdoc cref="WaitObservedAsync(Task, CancellationToken)"/>
  public static async Task<T> WaitObservedAsync<T>(this Task<T> task, CancellationToken token)
  {
    if (task == null) throw new ArgumentNullException(nameof(task));
    try
    {
      return await WaitCoreAsync(task, token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested)
    {
      ObserveFailure(task);
      throw;
    }
  }

  /// <summary>
  /// Reads the exception of <paramref name="task"/> if it fails, now or later.
  /// </summary>
  private static void ObserveFailure(Task task) =>
    task.ContinueWith(
      static t => _ = t.Exception,
      CancellationToken.None,
      TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default);

#if NET6_0_OR_GREATER
  private static Task WaitCoreAsync(Task task, CancellationToken token) => task.WaitAsync(token);

  private static Task<T> WaitCoreAsync<T>(Task<T> task, CancellationToken token) => task.WaitAsync(token);
#else
  private static async Task WaitCoreAsync(Task task, CancellationToken token)
  {
    if (!token.CanBeCanceled)
    {
      await task.ConfigureAwait(false);
      return;
    }
    // If the token can be cancelled, a second task is set up that completes when it is cancelled.
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var reg = token.Register(() => tcs.TrySetCanceled(token));
    var completed = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
    await completed.ConfigureAwait(false);
  }

  private static async Task<T> WaitCoreAsync<T>(Task<T> task, CancellationToken token)
  {
    if (!token.CanBeCanceled)
      return await task.ConfigureAwait(false);
    // If the token can be cancelled, a second task is set up that completes when it is cancelled.
    var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var reg = token.Register(() => tcs.TrySetCanceled(token));
    var completed = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
    return await completed.ConfigureAwait(false);
  }
#endif
}
