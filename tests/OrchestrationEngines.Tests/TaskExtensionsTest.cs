using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using OrchestrationEngines.Helpers;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the library's internal task helpers.
/// </summary>
public class TaskExtensionsTest
{
  /// <summary>
  /// The failure of the waited task in these tests; recognised among the unobserved exceptions of other tests.
  /// </summary>
  private sealed class WaitedTaskFailure(string test) : Exception(test);

  /// <summary>
  /// Verifies that a wait that is cancelled before its task fails leaves no unobserved task exception behind: the
  /// failure that comes later is read, so the finalizer does not report it (#86 item 6). The waited tasks must be
  /// collected first, or the finalizer has not run and the check proves nothing.
  /// </summary>
  [Fact]
  public async Task WaitObservedAsync_CancelledBeforeTheTaskFails_LeavesNoUnobservedException()
  {
    const string test = nameof(WaitObservedAsync_CancelledBeforeTheTaskFails_LeavesNoUnobservedException);
    var unobserved = new ConcurrentQueue<AggregateException>();
    void OnUnobserved(object sender, UnobservedTaskExceptionEventArgs e) =>
      unobserved.Enqueue(e.Exception);
    TaskScheduler.UnobservedTaskException += OnUnobserved;
    try
    {
      var waited = await CancelTheWaitThenFailTheTaskAsync(test);
      var waitedOfT = await CancelTheWaitThenFailTheTaskOfTAsync(test);
      (await Wait.UntilCollectedAsync(MustComplete, waited, waitedOfT)).Should().BeTrue(
        "the finalizer reports an unobserved failure only once the task is collected");
      GC.WaitForPendingFinalizers();
    }
    finally
    {
      TaskScheduler.UnobservedTaskException -= OnUnobserved;
    }

    unobserved
      .SelectMany(e => e.InnerExceptions)
      .OfType<WaitedTaskFailure>()
      .Where(e => e.Message == test)
      .Should().BeEmpty();
  }

  /// <summary>
  /// Verifies that a pending task gets one failure observer, however many waits on it are cancelled: a latch that a
  /// caller polls with short timeouts does not collect one per poll.
  /// </summary>
  [Fact]
  public async Task WaitObservedAsync_ManyCancelledWaits_LeaveOneObserver()
  {
    var latch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    for (var i = 0; i < 3; i++)
      await CancelAWaitAsync(latch.Task);

    OrchestrationEngines.Helpers.TaskExtensions.ObserveFailure(latch.Task).Should().BeFalse("the first cancelled wait attached the only observer");
    latch.SetResult(true);
  }

  /// <summary>
  /// Starts a wait for <paramref name="waited"/> and cancels it; the wait must end in a cancellation.
  /// </summary>
  private static async Task CancelAWaitAsync(Task waited)
  {
    using var cancel = new CancellationTokenSource();
    var wait = waited.WaitObservedAsync(cancel.Token);
    cancel.Cancel();
    await FluentActions.Awaiting(() => wait).Should().ThrowAsync<OperationCanceledException>();
  }

  /// <inheritdoc cref="CancelAWaitAsync"/>
  private static async Task CancelAWaitOfTAsync<T>(Task<T> waited)
  {
    using var cancel = new CancellationTokenSource();
    var wait = waited.WaitObservedAsync(cancel.Token);
    cancel.Cancel();
    await FluentActions.Awaiting(() => wait).Should().ThrowAsync<OperationCanceledException>();
  }

  // Not inlined, so the waited task is unreachable once these return.
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static async Task<WeakReference> CancelTheWaitThenFailTheTaskAsync(string test)
  {
    var waited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    await CancelAWaitAsync(waited.Task);
    waited.SetException(new WaitedTaskFailure(test));
    return new WeakReference(waited.Task);
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static async Task<WeakReference> CancelTheWaitThenFailTheTaskOfTAsync(string test)
  {
    var waited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    await CancelAWaitOfTAsync(waited.Task);
    waited.SetException(new WaitedTaskFailure(test));
    return new WeakReference(waited.Task);
  }
}
