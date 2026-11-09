
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OrchestrationEngines.Helpers;

/// <summary>
/// Provides helper members for task extensions.
/// </summary>
internal static class TaskExtensions
{
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
