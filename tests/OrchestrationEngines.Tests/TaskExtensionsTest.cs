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
  /// failure that comes later is read, so the finalizer does not report it (#86 item 6).
  /// </summary>
  [Fact]
  public async Task WaitObservedAsync_CancelledBeforeTheTaskFails_LeavesNoUnobservedException()
  {
    var unobserved = new ConcurrentQueue<AggregateException>();
    void OnUnobserved(object sender, UnobservedTaskExceptionEventArgs e) =>
      unobserved.Enqueue(e.Exception);
    TaskScheduler.UnobservedTaskException += OnUnobserved;
    try
    {
      await CancelTheWaitThenFailTheTaskAsync(nameof(WaitObservedAsync_CancelledBeforeTheTaskFails_LeavesNoUnobservedException));
      await CancelTheWaitThenFailTheTaskOfTAsync(nameof(WaitObservedAsync_CancelledBeforeTheTaskFails_LeavesNoUnobservedException));
      for (var i = 0; i < 3; i++)
      {
        GC.Collect();
        GC.WaitForPendingFinalizers();
      }
    }
    finally
    {
      TaskScheduler.UnobservedTaskException -= OnUnobserved;
    }

    unobserved
      .SelectMany(e => e.InnerExceptions)
      .OfType<WaitedTaskFailure>()
      .Where(e => e.Message == nameof(WaitObservedAsync_CancelledBeforeTheTaskFails_LeavesNoUnobservedException))
      .Should().BeEmpty();
  }

  // Not inlined, so the waited task is unreachable once these return.
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static async Task CancelTheWaitThenFailTheTaskAsync(string test)
  {
    var waited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cancel = new CancellationTokenSource();
    var wait = ((Task)waited.Task).WaitObservedAsync(cancel.Token);
    cancel.Cancel();
    await FluentActions.Awaiting(() => wait).Should().ThrowAsync<OperationCanceledException>();
    waited.SetException(new WaitedTaskFailure(test));
  }

  [MethodImpl(MethodImplOptions.NoInlining)]
  private static async Task CancelTheWaitThenFailTheTaskOfTAsync(string test)
  {
    var waited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cancel = new CancellationTokenSource();
    var wait = waited.Task.WaitObservedAsync(cancel.Token);
    cancel.Cancel();
    await FluentActions.Awaiting(() => wait).Should().ThrowAsync<OperationCanceledException>();
    waited.SetException(new WaitedTaskFailure(test));
  }
}
