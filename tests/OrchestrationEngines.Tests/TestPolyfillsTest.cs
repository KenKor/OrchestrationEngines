using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the waits the tests use, on every target: on net48 <c>Task.WaitAsync</c> is the test polyfill in
/// TestPolyfills.cs, and it must behave as the framework's does.
/// </summary>
public class TestPolyfillsTest
{
  /// <summary>
  /// Verifies that a wait on a task that is cancelled while the wait runs passes on the task's own token. A failure on
  /// net48 means the polyfill lost the token: fix the polyfill, not the tests that assert a caller's token.
  /// </summary>
  [Fact]
  public async Task WaitAsync_TaskCancelledWhileWaitedOn_PassesOnTheTasksToken()
  {
    var ct = TestContext.Current.CancellationToken;
    using var source = new CancellationTokenSource();
    source.Cancel();
    var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var pendingOfT = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

    var wait = pending.Task.WaitAsync(MustComplete, ct);
    var waitOfT = pendingOfT.Task.WaitAsync(MustComplete, ct);
    pending.TrySetCanceled(source.Token);
    pendingOfT.TrySetCanceled(source.Token);

    (await wait.Awaiting(w => w).Should().ThrowAsync<OperationCanceledException>())
      .Which.CancellationToken.Should().Be(source.Token);
    (await waitOfT.Awaiting(w => w).Should().ThrowAsync<OperationCanceledException>())
      .Which.CancellationToken.Should().Be(source.Token);
  }
}
