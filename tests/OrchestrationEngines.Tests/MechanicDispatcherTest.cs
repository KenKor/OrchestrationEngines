using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestrationEngines.Mechanic;
using Xunit;

namespace OrchestrationEngines.Tests;

/// <summary>
/// Contains tests for the error paths of the dispatcher that runs the coordinator's operations one at a time: a call
/// that throws, a caller that gives up while its call is queued, and calls that meet a shutdown.
/// </summary>
public class MechanicDispatcherTest
{
  private static readonly TimeSpan MustComplete = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Verifies that a call that throws faults only its own caller, and that the dispatcher goes on with the next call.
  /// </summary>
  [Fact]
  public async Task InvokeAsync_ActionThrows_FaultsOnlyThatCall()
  {
    var ct = TestContext.Current.CancellationToken;
    using var engineCts = new CancellationTokenSource();
    using var dispatcher = new MechanicDispatcher("Test", NullLogger.Instance, engineCts.Token);

    try
    {
      var failing = dispatcher.InvokeAsync(() => Task.FromException(new InvalidOperationException("Broken.")), ct);
      var next = dispatcher.InvokeAsync(() => Task.FromResult(42), ct);

      await failing.Awaiting(t => t.WaitAsync(MustComplete, ct)).Should().ThrowAsync<InvalidOperationException>()
        .WithMessage("Broken.");
      (await next.WaitAsync(MustComplete, ct)).Should().Be(42);
    }
    finally
    {
      engineCts.Cancel();
    }
  }

  /// <summary>
  /// Verifies that a call whose caller cancels while it waits behind another call is cancelled with the caller's
  /// token without running.
  /// </summary>
  [Fact]
  public async Task InvokeAsync_CallerCancelsWhileQueued_CancelsWithoutRunning()
  {
    var ct = TestContext.Current.CancellationToken;
    using var engineCts = new CancellationTokenSource();
    using var dispatcher = new MechanicDispatcher("Test", NullLogger.Instance, engineCts.Token);
    var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var callerCts = new CancellationTokenSource();
    var ran = false;

    try
    {
      var blocking = dispatcher.InvokeAsync(() => blocker.Task, ct);
      var queued = dispatcher.InvokeAsync(() => { ran = true; return Task.CompletedTask; }, callerCts.Token);

      callerCts.Cancel();
      blocker.SetResult();
      await blocking.WaitAsync(MustComplete, ct);

      var cancelled = await queued.Awaiting(t => t.WaitAsync(MustComplete, ct)).Should().ThrowAsync<OperationCanceledException>();
      cancelled.Which.CancellationToken.Should().Be(callerCts.Token);
      ran.Should().BeFalse();
    }
    finally
    {
      blocker.TrySetResult();
      engineCts.Cancel();
    }
  }

  /// <summary>
  /// Verifies that calls still queued when the engine token is cancelled are cancelled with that token, and that a
  /// call made after that is cancelled at once instead of waiting for a dispatcher that no longer runs.
  /// </summary>
  [Fact]
  public async Task InvokeAsync_EngineTokenCancelled_CancelsQueuedAndLaterCalls()
  {
    var ct = TestContext.Current.CancellationToken;
    using var engineCts = new CancellationTokenSource();
    using var dispatcher = new MechanicDispatcher("Test", NullLogger.Instance, engineCts.Token);
    var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    try
    {
      var blocking = dispatcher.InvokeAsync(() => { running.SetResult(); return blocker.Task; }, ct);
      var queued = dispatcher.InvokeAsync(() => Task.CompletedTask, ct);
      // Cancel only once the first call runs: a call the worker has not picked up yet would be cancelled as well.
      await running.Task.WaitAsync(MustComplete, ct);

      engineCts.Cancel();
      blocker.SetResult();
      await blocking.WaitAsync(MustComplete, ct);

      var cancelled = await queued.Awaiting(t => t.WaitAsync(MustComplete, ct)).Should().ThrowAsync<OperationCanceledException>();
      cancelled.Which.CancellationToken.Should().Be(engineCts.Token);

      var later = dispatcher.InvokeAsync(() => Task.CompletedTask, ct);
      await later.Awaiting(t => t.WaitAsync(MustComplete, ct)).Should().ThrowAsync<OperationCanceledException>();
    }
    finally
    {
      blocker.TrySetResult();
    }
  }

  /// <summary>
  /// Verifies that a null action is rejected.
  /// </summary>
  [Fact]
  public void InvokeAsync_NullAction_Throws()
  {
    using var dispatcher = new MechanicDispatcher("Test", NullLogger.Instance, CancellationToken.None);

    Action invoke = () => dispatcher.InvokeAsync<int>(null);

    invoke.Should().Throw<ArgumentNullException>();
  }
}
